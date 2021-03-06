﻿//-----------------------------------------------------------------------
// <copyright file="WebApiToSwaggerGeneratorSettings.cs" company="NSwag">
//     Copyright (c) Rico Suter. All rights reserved.
// </copyright>
// <license>https://github.com/NSwag/NSwag/blob/master/LICENSE.md</license>
// <author>Rico Suter, mail@rsuter.com</author>
//-----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NJsonSchema;
using NJsonSchema.Annotations;
using NJsonSchema.Generation;
using NJsonSchema.Infrastructure;
using NSwag.SwaggerGeneration.Processors;
using NSwag.SwaggerGeneration.Processors.Contexts;

namespace NSwag.SwaggerGeneration.AspNetCore.Processors
{
    internal class OperationParameterProcessor : IOperationProcessor
    {
        private readonly AspNetCoreToSwaggerGeneratorSettings _settings;

        public OperationParameterProcessor(AspNetCoreToSwaggerGeneratorSettings settings)
        {
            _settings = settings;
        }

        /// <summary>Processes the specified method information.</summary>
        /// <param name="operationProcessorContext"></param>
        /// <returns>true if the operation should be added to the Swagger specification.</returns>
        public async Task<bool> ProcessAsync(OperationProcessorContext operationProcessorContext)
        {
            if (!(operationProcessorContext is AspNetCoreOperationProcessorContext context))
            {
                return false;
            }

            var httpPath = context.OperationDescription.Path;
            var parameters = context.ApiDescription.ParameterDescriptions;

            var methodParameters = context.MethodInfo.GetParameters();

            foreach (var apiParameter in parameters.Where(p => p.Source != null))
            {
                var parameterDescriptor = apiParameter.TryGetPropertyValue<ParameterDescriptor>("ParameterDescriptor");
                var parameterName = parameterDescriptor?.Name ?? apiParameter.Name;

                // In Mvc < 2.0, there isn't a good way to infer the attributes of a parameter with a IModelNameProvider.Name
                // value that's different than the parameter name. Additionally, ApiExplorer will recurse in to complex model bound types
                // and expose properties as top level parameters. Consequently, determining the property or parameter of an Api is best
                // effort attempt.
                var extendedApiParameter = new ExtendedApiParameterDescription
                {
                    ApiParameter = apiParameter,
                    Attributes = Enumerable.Empty<Attribute>(),
                    ParameterType = apiParameter.Type
                };

                var parameter = methodParameters.FirstOrDefault(m => m.Name == parameterName);
                if (parameter != null)
                {
                    extendedApiParameter.ParameterInfo = parameter;
                    extendedApiParameter.Attributes = parameter.GetCustomAttributes();
                }
                else
                {
                    var property = operationProcessorContext.ControllerType.GetProperty(parameterName, BindingFlags.Public | BindingFlags.Instance);
                    if (property != null)
                    {
                        extendedApiParameter.PropertyInfo = property;
                        extendedApiParameter.Attributes = property.GetCustomAttributes();
                    }
                }

                if (apiParameter.Type == null)
                {
                    extendedApiParameter.ParameterType = typeof(string);

                    if (apiParameter.Source == BindingSource.Path)
                    {
                        // ignore unused implicit path parameters
                        if (!httpPath.ToLowerInvariant().Contains("{" + apiParameter.Name.ToLowerInvariant() + ":") &&
                            !httpPath.ToLowerInvariant().Contains("{" + apiParameter.Name.ToLowerInvariant() + "}"))
                        {
                            continue;
                        }

                        extendedApiParameter.Attributes = extendedApiParameter.Attributes.Concat(new[] { new NotNullAttribute() });
                    }
                }

                if (extendedApiParameter.Attributes.Any(a => a.GetType().Name == "SwaggerIgnoreAttribute"))
                {
                    continue;
                }

                SwaggerParameter operationParameter = null;
                if (apiParameter.Source == BindingSource.Path)
                {
                    operationParameter = await CreatePrimitiveParameterAsync(context, extendedApiParameter).ConfigureAwait(false);
                    operationParameter.Kind = SwaggerParameterKind.Path;
                    operationParameter.IsRequired = true; // apiParameter.RouteInfo?.IsOptional == false;

                    context.OperationDescription.Operation.Parameters.Add(operationParameter);
                }
                else if (apiParameter.Source == BindingSource.Header)
                {
                    operationParameter = await CreatePrimitiveParameterAsync(context, extendedApiParameter).ConfigureAwait(false);
                    operationParameter.Kind = SwaggerParameterKind.Header;

                    context.OperationDescription.Operation.Parameters.Add(operationParameter);
                }
                else if (apiParameter.Source == BindingSource.Query)
                {
                    operationParameter = await CreatePrimitiveParameterAsync(context, extendedApiParameter).ConfigureAwait(false);
                    operationParameter.Kind = SwaggerParameterKind.Query;

                    context.OperationDescription.Operation.Parameters.Add(operationParameter);
                }
                else if (apiParameter.Source == BindingSource.Body)
                {
                    operationParameter = await AddBodyParameterAsync(context, extendedApiParameter).ConfigureAwait(false);
                }
                else if (apiParameter.Source == BindingSource.Form)
                {
                    operationParameter = await CreatePrimitiveParameterAsync(context, extendedApiParameter).ConfigureAwait(false);
                    operationParameter.Kind = SwaggerParameterKind.FormData;

                    context.OperationDescription.Operation.Parameters.Add(operationParameter);
                }
                else
                {
                    if (await TryAddFileParameterAsync(context, extendedApiParameter).ConfigureAwait(false) == false)
                    {
                        operationParameter = await CreatePrimitiveParameterAsync(context, extendedApiParameter).ConfigureAwait(false);
                        operationParameter.Kind = SwaggerParameterKind.Query;

                        context.OperationDescription.Operation.Parameters.Add(operationParameter);
                    }
                }

                if (parameter != null && operationParameter != null)
                {
                    ((Dictionary<ParameterInfo, SwaggerParameter>)operationProcessorContext.Parameters)[parameter] = operationParameter;
                }
            }

            RemoveUnusedPathParameters(context.OperationDescription, httpPath);
            UpdateConsumedTypes(context.OperationDescription);

            EnsureSingleBodyParameter(context.OperationDescription);

            return true;
        }

        private void EnsureSingleBodyParameter(SwaggerOperationDescription operationDescription)
        {
            if (operationDescription.Operation.ActualParameters.Count(p => p.Kind == SwaggerParameterKind.Body) > 1)
                throw new InvalidOperationException($"The operation '{operationDescription.Operation.OperationId}' has more than one body parameter.");
        }

        private void UpdateConsumedTypes(SwaggerOperationDescription operationDescription)
        {
            if (operationDescription.Operation.ActualParameters.Any(p => p.Type == JsonObjectType.File))
                operationDescription.Operation.Consumes = new List<string> { "multipart/form-data" };
        }

        private void RemoveUnusedPathParameters(SwaggerOperationDescription operationDescription, string httpPath)
        {
            operationDescription.Path = Regex.Replace(httpPath, "{(.*?)(:(([^/]*)?))?}", match =>
            {
                var parameterName = match.Groups[1].Value.TrimEnd('?');
                if (operationDescription.Operation.ActualParameters.Any(p => p.Kind == SwaggerParameterKind.Path && string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase)))
                    return "{" + parameterName + "}";
                return string.Empty;
            }).TrimEnd('/');
        }

        private bool IsNullable(ParameterInfo parameter)
        {
            var isNullable = Nullable.GetUnderlyingType(parameter.ParameterType) != null;
            if (isNullable)
                return false;

            return parameter.ParameterType.GetTypeInfo().IsValueType;
        }

        private async Task<bool> TryAddFileParameterAsync(
            OperationProcessorContext context, ExtendedApiParameterDescription extendedApiParameter)
        {
            var info = _settings.ReflectionService.GetDescription(extendedApiParameter.ParameterType, extendedApiParameter.Attributes, _settings);

            var isFileArray = IsFileArray(extendedApiParameter.ApiParameter.Type, info);

            var attributes = extendedApiParameter.Attributes
                .Union(extendedApiParameter.ParameterType.GetTypeInfo().GetCustomAttributes());

            var hasSwaggerFileAttribute = attributes.Any(a =>
                a.GetType().IsAssignableTo("SwaggerFileAttribute", TypeNameStyle.Name));

            if (info.Type == JsonObjectType.File || hasSwaggerFileAttribute || isFileArray)
            {
                await AddFileParameterAsync(context, extendedApiParameter, isFileArray).ConfigureAwait(false);
                return true;
            }

            return false;
        }

        private async Task AddFileParameterAsync(OperationProcessorContext context, ExtendedApiParameterDescription extendedApiParameter, bool isFileArray)
        {
            var operationParameter = await CreatePrimitiveParameterAsync(context, extendedApiParameter).ConfigureAwait(false);
            InitializeFileParameter(operationParameter, isFileArray);

            context.OperationDescription.Operation.Parameters.Add(operationParameter);
        }

        private bool IsFileArray(Type type, JsonTypeDescription typeInfo)
        {
            var isFormFileCollection = type.Name == "IFormFileCollection";
            var isFileArray = typeInfo.Type == JsonObjectType.Array && type.GenericTypeArguments.Any() &&
                _settings.ReflectionService.GetDescription(type.GenericTypeArguments[0], null, _settings).Type == JsonObjectType.File;
            return isFormFileCollection || isFileArray;
        }

        private async Task<SwaggerParameter> AddBodyParameterAsync(OperationProcessorContext context, ExtendedApiParameterDescription extendedApiParameter)
        {
            SwaggerParameter operationParameter;

            var operation = context.OperationDescription.Operation;
            var parameterType = extendedApiParameter.ParameterType;
            if (parameterType.Name == "XmlDocument" || parameterType.InheritsFrom("XmlDocument", TypeNameStyle.Name))
            {
                operation.Consumes = new List<string> { "application/xml" };
                operationParameter = new SwaggerParameter
                {
                    Name = extendedApiParameter.ApiParameter.Name,
                    Kind = SwaggerParameterKind.Body,
                    Schema = new JsonSchema4 { Type = JsonObjectType.String },
                    IsNullableRaw = true,
                    IsRequired = true,
                    Description = await extendedApiParameter.GetDocumentationAsync().ConfigureAwait(false)
                };
            }
            else if (parameterType.IsAssignableTo("System.IO.Stream", TypeNameStyle.FullName))
            {
                operation.Consumes = new List<string> { "application/octet-stream" };
                operationParameter = new SwaggerParameter
                {
                    Name = extendedApiParameter.ApiParameter.Name,
                    Kind = SwaggerParameterKind.Body,
                    Schema = new JsonSchema4 { Type = JsonObjectType.String, Format = JsonFormatStrings.Byte },
                    IsNullableRaw = true,
                    IsRequired = true,
                    Description = await extendedApiParameter.GetDocumentationAsync().ConfigureAwait(false)
                };
            }
            else
            {
                var typeDescription = _settings.ReflectionService.GetDescription(extendedApiParameter.ParameterType, extendedApiParameter.Attributes, _settings);

                operationParameter = new SwaggerParameter
                {
                    Name = extendedApiParameter.ApiParameter.Name,
                    Kind = SwaggerParameterKind.Body,
                    IsRequired = true, // FromBody parameters are always required.
                    IsNullableRaw = typeDescription.IsNullable,
                    Description = await extendedApiParameter.GetDocumentationAsync().ConfigureAwait(false),
                    Schema = await context.SchemaGenerator.GenerateWithReferenceAndNullabilityAsync<JsonSchema4>(
                        extendedApiParameter.ParameterType, extendedApiParameter.Attributes, isNullable: false, schemaResolver: context.SchemaResolver).ConfigureAwait(false)
                };
            }

            operation.Parameters.Add(operationParameter);
            return operationParameter;
        }

        private async Task<SwaggerParameter> CreatePrimitiveParameterAsync(
            OperationProcessorContext context,
            ExtendedApiParameterDescription extendedApiParameter)
        {
            var operationParameter = await context.SwaggerGenerator.CreatePrimitiveParameterAsync(
                extendedApiParameter.ApiParameter.Name,
                await extendedApiParameter.GetDocumentationAsync().ConfigureAwait(false),
                extendedApiParameter.ParameterType,
                extendedApiParameter.Attributes).ConfigureAwait(false);

            if (extendedApiParameter.ParameterInfo?.HasDefaultValue == true)
            {
                operationParameter.Default = extendedApiParameter.ParameterInfo.DefaultValue;
            }

            operationParameter.IsRequired = extendedApiParameter.IsRequired;
            return operationParameter;
        }

        private void InitializeFileParameter(SwaggerParameter operationParameter, bool isFileArray)
        {
            operationParameter.Type = JsonObjectType.File;
            operationParameter.Kind = SwaggerParameterKind.FormData;

            if (isFileArray)
                operationParameter.CollectionFormat = SwaggerParameterCollectionFormat.Multi;
        }

        private class ExtendedApiParameterDescription
        {
            public ApiParameterDescription ApiParameter { get; set; }

            public ParameterInfo ParameterInfo { get; set; }

            public PropertyInfo PropertyInfo { get; set; }

            public Type ParameterType { get; set; }

            public IEnumerable<Attribute> Attributes { get; set; }

            public bool IsRequired
            {
                get
                {
                    return ParameterInfo?.HasDefaultValue != true;

                    //// available in asp.net core >= 2.2
                    //if (ApiParameter.HasProperty("IsRequired"))
                    //{
                    //    return ApiParameter.TryGetPropertyValue("IsRequired", false);
                    //}

                    //// fallback for asp.net core <= 2.1
                    //if (ApiParameter.Source == BindingSource.Body)
                    //{
                    //    return true;
                    //}

                    //if (ApiParameter.ModelMetadata != null &&
                    //    ApiParameter.ModelMetadata.IsBindingRequired)

                    //{
                    //    return true;
                    //}

                    //if (ApiParameter.Source == BindingSource.Path &&
                    //    ApiParameter.RouteInfo != null &&
                    //    ApiParameter.RouteInfo.IsOptional == false)
                    //{
                    //    return true;
                    //}

                    //return false;
                }
            }

            public async Task<string> GetDocumentationAsync()
            {
                var parameterDocumentation = string.Empty;
                if (ParameterInfo != null)
                {
                    parameterDocumentation = await ParameterInfo.GetDescriptionAsync(Attributes).ConfigureAwait(false);
                }
                else if (PropertyInfo != null)
                {
                    parameterDocumentation = await PropertyInfo.GetDescriptionAsync(Attributes).ConfigureAwait(false);
                }

                return parameterDocumentation;
            }
        }
    }
}