﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using TypeGen.Core.Converters;
using TypeGen.Core.Extensions;
using TypeGen.Core.Storage;
using TypeGen.Core.TypeAnnotations;
using TypeGen.Core.Utils;

namespace TypeGen.Core.Business
{
    /// <summary>
    /// Contains logic for generating TypeScript file contents
    /// </summary>
    internal class TsContentGenerator
    {
        private readonly TypeDependencyService _typeDependencyService;
        private readonly TypeService _typeService;
        private readonly TemplateService _templateService;
        private readonly FileSystem _fileSystem;
        private readonly TsContentParser _tsContentParser;

        private const string KeepTsTagName = "keep-ts";
        private const string CustomHeadTagName = "custom-head";
        private const string CustomBodyTagName = "custom-body";

        public TsContentGenerator(TypeDependencyService typeDependencyService,
            TypeService typeService,
            TemplateService templateService,
            FileSystem fileSystem,
            TsContentParser tsContentParser)
        {
            _typeDependencyService = typeDependencyService;
            _typeService = typeService;
            _templateService = templateService;
            _fileSystem = fileSystem;
            _tsContentParser = tsContentParser;
        }

        /// <summary>
        /// Gets code for the 'imports' section for a given type
        /// </summary>
        /// <param name="type"></param>
        /// <param name="outputDir"></param>
        /// <param name="fileNameConverters"></param>
        /// <param name="typeNameConverters"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException">Thrown when one of: type, fileNameConverters or typeNameConverters is null</exception>
        public string GetImportsText(Type type, string outputDir, TypeNameConverterCollection fileNameConverters, TypeNameConverterCollection typeNameConverters)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (fileNameConverters == null) throw new ArgumentNullException(nameof(fileNameConverters));
            if (typeNameConverters == null) throw new ArgumentNullException(nameof(typeNameConverters));

            string result = GetTypeDependencyImportsText(type, outputDir, fileNameConverters, typeNameConverters);
            result += GetCustomImportsText(type);

            if (!string.IsNullOrEmpty(result))
            {
                result += "\r\n";
            }

            return result;
        }

        /// <summary>
        /// Gets the text for the "extends" section
        /// </summary>
        /// <param name="type"></param>
        /// <param name="typeNameConverters"></param>
        /// <returns></returns>
        public string GetExtendsText(Type type, TypeNameConverterCollection typeNameConverters)
        {
            var extendsText = "";

            Type baseType = _typeService.GetBaseType(type);
            if (baseType == null) return extendsText;

            string baseTypeName = _typeService.GetTsTypeName(baseType, typeNameConverters);
            extendsText = $" extends {baseTypeName}";

            return extendsText;
        }

        /// <summary>
        /// Returns TypeScript imports source code related to type dependencies
        /// </summary>
        /// <param name="type"></param>
        /// <param name="outputDir"></param>
        /// <param name="fileNameConverters"></param>
        /// <param name="typeNameConverters"></param>
        /// <returns></returns>
        private string GetTypeDependencyImportsText(Type type, string outputDir, TypeNameConverterCollection fileNameConverters, TypeNameConverterCollection typeNameConverters)
        {
            var result = "";
            IEnumerable<TypeDependencyInfo> typeDependencies = _typeDependencyService.GetTypeDependencies(type);

            foreach (TypeDependencyInfo typeDependencyInfo in typeDependencies)
            {
                Type typeDependency = typeDependencyInfo.Type;

                string dependencyOutputDir = GetTypeDependencyOutputDir(typeDependencyInfo, outputDir);

                // get path diff
                string pathDiff = _fileSystem.GetPathDiff(outputDir, dependencyOutputDir);
                pathDiff = pathDiff.StartsWith("..\\") || pathDiff.StartsWith("../") ? pathDiff : $"./{pathDiff}";

                // get type & file name
                string typeDependencyName = typeDependency.Name.RemoveTypeArity();
                string fileName = fileNameConverters.Convert(typeDependencyName, typeDependency);

                // get file path
                string dependencyPath = pathDiff + fileName;
                dependencyPath = dependencyPath.Replace('\\', '/');

                string typeName = typeNameConverters.Convert(typeDependencyName, typeDependency);
                result += _templateService.FillImportTemplate(typeName, "", dependencyPath);
            }

            return result;
        }

        /// <summary>
        /// Gets code for imports that are specified in TsTypeAttribute.ImportPath property
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        private string GetCustomImportsText(Type type)
        {
            var result = "";
            IEnumerable<MemberInfo> members = _typeService.GetTsExportableMembers(type);

            IEnumerable<TsTypeAttribute> typeAttributes = members
                .Select(memberInfo => memberInfo.GetCustomAttribute<TsTypeAttribute>())
                .Where(tsTypeAttribute => !string.IsNullOrEmpty(tsTypeAttribute?.ImportPath))
                .Distinct(new TsTypeAttributeComparer());

            foreach (TsTypeAttribute attribute in typeAttributes)
            {
                bool withOriginalTypeName = !string.IsNullOrEmpty(attribute.OriginalTypeName);

                string name = withOriginalTypeName ? attribute.OriginalTypeName : attribute.FlatTypeName;
                string asAlias = withOriginalTypeName ? $" as {attribute.FlatTypeName}" : "";
                result += _templateService.FillImportTemplate(name, asAlias, attribute.ImportPath);
            }

            return result;
        }

        /// <summary>
        /// Gets the output directory for a type dependency
        /// </summary>
        /// <param name="typeDependencyInfo"></param>
        /// <param name="exportedTypeOutputDir"></param>
        /// <returns></returns>
        private string GetTypeDependencyOutputDir(TypeDependencyInfo typeDependencyInfo, string exportedTypeOutputDir)
        {
            var classAttribute = typeDependencyInfo.Type.GetTypeInfo().GetCustomAttribute<ExportTsClassAttribute>();
            var interfaceAttribute = typeDependencyInfo.Type.GetTypeInfo().GetCustomAttribute<ExportTsInterfaceAttribute>();
            var enumAttribute = typeDependencyInfo.Type.GetTypeInfo().GetCustomAttribute<ExportTsEnumAttribute>();

            if (classAttribute == null && enumAttribute == null && interfaceAttribute == null)
            {
                TsDefaultTypeOutputAttribute defaultTypeOutputAttribute = typeDependencyInfo.MemberAttributes
                    ?.SingleOrDefault(a => a.GetType() == typeof(TsDefaultTypeOutputAttribute))
                    as TsDefaultTypeOutputAttribute;

                return defaultTypeOutputAttribute?.OutputDir ?? exportedTypeOutputDir;
            }

            return classAttribute?.OutputDir
                    ?? interfaceAttribute?.OutputDir
                    ?? enumAttribute?.OutputDir;
        }

        /// <summary>
        /// Gets custom code for a TypeScript file given by filePath.
        /// Returns an empty string if a file does not exist.
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="indentSize"></param>
        /// <returns></returns>
        public string GetCustomBody(string filePath, int indentSize)
        {
            string content = _tsContentParser.GetTagContent(filePath, indentSize, KeepTsTagName, CustomBodyTagName);
            string tab = StringUtils.GetTabText(indentSize);

            return string.IsNullOrEmpty(content)
                ? ""
                : $"\r\n\r\n{tab}//<{CustomBodyTagName}>\r\n{tab}{content}{tab}//</{CustomBodyTagName}>";
        }

        /// <summary>
        /// Gets custom code for a TypeScript file given by filePath.
        /// Returns an empty string if a file does not exist.
        /// </summary>
        /// <param name="filePath"></param>
        /// <returns></returns>
        public string GetCustomHead(string filePath)
        {
            string content = _tsContentParser.GetTagContent(filePath, 0, CustomHeadTagName);

            return string.IsNullOrEmpty(content)
                ? ""
                : $"//<{CustomHeadTagName}>\r\n{content}//</{CustomHeadTagName}>\r\n\r\n";
        }
    }
}
