﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using AutoWasmApiGenerator.Extensions;
using Generators.Models;
using Generators.Shared;
using Generators.Shared.Builder;
using Microsoft.CodeAnalysis;
using static AutoWasmApiGenerator.GeneratorHelpers;

namespace AutoWasmApiGenerator;

public class HttpServiceInvokerGeneratorImpl : IHttpServiceInvokerGenerator
{
    // 0 - Query
    // 1 - Route
    // 2 - Form
    // 3 - Body
    // 4 - Header
    private static class WebMethodConstants
    {
        public const string Get = "Get";
        public const string Post = "Post";
        public const string Put = "Put";
        public const string Delete = "Delete";
    }
    private enum ParameterBindingType
    {
        Ignore = -1,
        FromQuery = 0,
        FromRoute = 1,
        FromForm = 2,
        FromBody = 3,
        FromHeader = 4
    }

    public (string generatedFileName, string sourceCode, List<Diagnostic> errorAndWarnings) Generate(
        INamedTypeSymbol interfaceSymbol)
    {
        var errorAndWarnings = new List<Diagnostic>();
        var ret = CreateCodeFile(interfaceSymbol, errorAndWarnings, out var file);
        return ret
            ? (file!.FileName, file.ToString(), errorAndWarnings)
            : ("", "", errorAndWarnings);
    }

    private static bool CreateCodeFile(INamedTypeSymbol interfaceSymbol, List<Diagnostic> errorAndWarnings,
        [NotNullWhen(true)] out CodeFile? file)
    {
        var methods = interfaceSymbol.GetAllMethodWithAttribute(WebMethodAttributeFullName);
        List<Node> members = new();
        _ = interfaceSymbol.GetAttribute(WebControllerAttributeFullName, out var controllerAttrData);
        var invokeClass = CreateHttpClassBuilder(interfaceSymbol);
        var scopeName = interfaceSymbol.FormatClassName();
        var route = controllerAttrData.GetNamedValue("Route") as string;
        var needAuth = (bool)(controllerAttrData.GetNamedValue("Authorize") ?? false);
        foreach (var method in methods)
        {
            var methodBuilder = BuildMethod(method, route, scopeName, needAuth, out var n, errorAndWarnings);
            if (n && !needAuth)
            {
                needAuth = true;
            }

            if (errorAndWarnings.Count > 0)
            {
                file = null;
                return false;
            }

            members.Add(methodBuilder!);
        }

        var fields = BuildField(needAuth);
        var constructor = BuildConstructor(interfaceSymbol, needAuth);
        members.AddRange(fields);
        members.Add(constructor);

        var classBuilder = invokeClass.AddMembers([.. members]);
        var ns = NamespaceBuilder.Default.Namespace(interfaceSymbol.ContainingNamespace.ToDisplayString());
        var namespaceBuilder = ns.AddMembers(classBuilder);
        file = CodeFile.New($"{interfaceSymbol.FormatFileName()}ApiInvoker.g.cs")
            .AddUsings("using Microsoft.Extensions.DependencyInjection;")
            .AddFileHeader("""
                           // <auto-generated/>
                           #pragma warning disable
                           #nullable enable
                           """)
            .AddMembers(namespaceBuilder);

        return true;
    }

    private static MethodBuilder? BuildMethod((IMethodSymbol methodSymbol, AttributeData? methodAttribute) method, string? route,
        string scopeName, bool controllerAuth, out bool needAuth, List<Diagnostic> errorAndWarnings)
    {
        var methodSymbol = method.Item1;
        var methodAttribute = method.Item2;

        var allowsAnonymous = (bool)(methodAttribute.GetNamedValue("AllowAnonymous") ?? false);
        var authorize = (bool)(methodAttribute.GetNamedValue("Authorize") ?? false);
        needAuth = !allowsAnonymous && (authorize || controllerAuth);
        var cancellationTokenName = methodSymbol.Parameters.Where(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.CancellationToken").Select(p => p.Name).FirstOrDefault();
        var hasCancellationToken = cancellationTokenName != null;

        if (methodSymbol.HasAttribute(NotSupported))
        {
            TypeParameterInfo[] typeParameterInfos = [.. methodSymbol.GetTypeParameters()];
            var rt = methodSymbol.ReturnType;
            var type = rt.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            string[] parameters = [.. methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}")];
            var b = MethodBuilder.Default
                .MethodName(methodSymbol.Name)
                .Generic(typeParameterInfos)
                .ReturnType(type)
                .AddParameter(parameters)
                .AddGeneratedCodeAttribute(typeof(HttpServiceInvokerGenerator))
                .Lambda("throw new global::System.NotSupportedException()");
            return b;
        }

        // 检查当前返回类型是否是Task或Task<T>
        // 如果检查类型不符合要求，说明不是异步方法
        // 返回错误信息
        var returnTypeInfo = methodSymbol.ReturnType;
        var isTask = returnTypeInfo.Name == "Task";
        var isGenericTask = returnTypeInfo.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .StartsWith("global::System.Threading.Tasks.Task<");

        if (!isTask && !isGenericTask)
        {
            errorAndWarnings.Add(DiagnosticDefinitions.WAG00005(methodSymbol.Locations.FirstOrDefault()));
            return null;
        }

        var webMethod = methodAttribute.GetNamedValue("Method", out var v) ? WebMethod[(int)v!] : "Post";
        var methodScoped = methodSymbol.Name.Replace("Async", "");
        var customRoute = methodAttribute?.GetNamedValue("Route")?.ToString();
        string methodRoute;
        var useRouteParam = false;
        if (string.IsNullOrEmpty(customRoute))
        {
            methodRoute = methodScoped;
        }
        else if (Regex.Match(customRoute, "{.+}").Success)
        {
            useRouteParam = true;
            methodRoute = $"{methodScoped}/{customRoute}";
        }
        else
        {
            methodRoute = customRoute!;
        }

        //var methodRoute = $"{methodAttribute?.GetNamedValue("Route") ?? methodSymbol.Name.Replace("Async", "")}";
        List<Statement> statements =
        [
            // var url = "";
            // var client = clientFactory.CreateClient(nameof(<TYPE>));
            $"var _client_gen = this.clientFactory.CreateClient(\"{scopeName}\")",
            // var request = new HttpRequestMessage();
            "var _request_gen = new global::System.Net.Http.HttpRequestMessage()",
            // request.Method = HttpMethod.<Method>
            $"_request_gen.Method = global::System.Net.Http.HttpMethod.{webMethod}"
        ];
        if (needAuth)
        {
            statements.Add($"await headerHandler.SetRequestHeaderAsync(_request_gen, {cancellationTokenName ?? "global::System.Threading.CancellationToken.None"})");
        }

        // 处理参数标签 
        var paramInfos = methodSymbol.Parameters.Select(p =>
        {
            if (p.GetAttribute(WebMethodParameterBindingAttribute, out var ad))
            {
                ad!.GetConstructorValue(0, out var bindingType);
                var t = (ParameterBindingType)(int)bindingType!;
                return (bindingType: t, p);
            }

            if (useRouteParam && customRoute!.Contains(p.Name))
            {
                return (bindingType: ParameterBindingType.FromRoute, p);
            }

            return (bindingType: ParameterBindingType.Ignore, p);
        }).ToList();

        #region 检查参数配置

        var routerParameters = paramInfos.Where(t => t.bindingType == ParameterBindingType.FromRoute);
        foreach (var item in routerParameters)
        {
            // 如果路由参数中包含方法名，则忽略
            if (methodRoute.Contains($"{{{item.p.Name}}}")) continue;
            errorAndWarnings.Add(DiagnosticDefinitions.WAG00006(methodSymbol.Locations.FirstOrDefault(),
                    methodSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            return null;
        }

        if (paramInfos.Any(t => t.bindingType == ParameterBindingType.FromForm) && paramInfos.Any(t => t.bindingType == ParameterBindingType.FromBody))
        {
            // 不能同时存在FromBody和FromForm
            errorAndWarnings.Add(DiagnosticDefinitions.WAG00007(methodSymbol.Locations.FirstOrDefault(),
                methodSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            return null;
        }

        #endregion

        var url = $"""
                   var _url_gen = $"api/{route ?? scopeName}/{methodRoute}"
                   """;
        statements.Add(url);

        AddQueryParameters(statements, paramInfos, webMethod, errorAndWarnings);
        AddFormParameters(statements, paramInfos);
        AddBodyParameters(statements, paramInfos, webMethod, methodSymbol, errorAndWarnings);
        AddHeaderParameters(statements, paramInfos);

        statements.Add("_request_gen.RequestUri = new global::System.Uri(_url_gen, UriKind.Relative)");
        var returnType = methodSymbol.ReturnType.GetGenericTypes().FirstOrDefault() ?? methodSymbol.ReturnType;

        if (methodSymbol.ReturnsVoid || (isTask && !isGenericTask))
        {
            statements.Add(hasCancellationToken
                ? $"_ = await _client_gen.SendAsync(_request_gen, {cancellationTokenName})"
                : "_ = await _client_gen.SendAsync(_request_gen)");
        }
        else
        {
            statements.Add(hasCancellationToken
                ? $"var _response_gen = await _client_gen.SendAsync(_request_gen, {cancellationTokenName})"
                : "var _response_gen = await _client_gen.SendAsync(_request_gen)");
            statements.Add("_response_gen.EnsureSuccessStatusCode()");
            AddResponseHandling(statements, returnType, errorAndWarnings, cancellationTokenName, methodSymbol);
        }

        var parameter = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}").ToArray();
        var builder = MethodBuilder.Default
            .MethodName(methodSymbol.Name)
            .Generic(methodSymbol.GetTypeParameters().ToArray())
            .Async()
            .ReturnType(methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
            .AddParameter(parameter)
            .AddGeneratedCodeAttribute(typeof(HttpServiceInvokerGenerator))
            .AddBody(statements.ToArray());
        return builder;
    }

    private static IEnumerable<FieldBuilder> BuildField(bool needAuth)
    {
        // private readonly JsonSerializerOptions jsonOptions;
        yield return FieldBuilder.Default.MemberType("global::System.Text.Json.JsonSerializerOptions")
            .FieldName("_JSON_OPTIONS_gen");
        // private readonly IHttpClientFactory clientFactory;
        yield return FieldBuilder.Default
            .MemberType("global::System.Net.Http.IHttpClientFactory")
            .FieldName("clientFactory");
        if (needAuth)
        {
            yield return FieldBuilder.Default
                .MemberType("global::AutoWasmApiGenerator.IHttpClientHeaderHandler")
                .FieldName("headerHandler");
        }
    }

    /// <summary>
    /// 处理Query参数
    /// </summary>
    /// <param name="statements"></param>
    /// <param name="paramInfos"></param>
    /// <param name="webMethod"></param>
    /// <param name="errorAndWarnings"></param>
    private static void AddQueryParameters(List<Statement> statements,
        IEnumerable<(ParameterBindingType i, IParameterSymbol p)> paramInfos, string webMethod, List<Diagnostic> errorAndWarnings)
    {
        var queryParameters = paramInfos.Where(t => (t.i == ParameterBindingType.Ignore && webMethod == WebMethodConstants.Get) || t.i == ParameterBindingType.FromQuery).ToList();

        if (!queryParameters.Any()) return;
        statements.Add("var _queries_gen = new global::System.Collections.Generic.List<string>()");
        foreach (var item in queryParameters)
        {
            var p = item.p;
            if (p.Type is INamedTypeSymbol
                {
                    TypeKind: TypeKind.Class, SpecialType: not SpecialType.System_String
                } parameterClassType)
            {
                var properties = parameterClassType.GetMembers().Where(m => m.Kind == SymbolKind.Property);
                foreach (var prop in properties)
                {
                    statements.Add(
                        $$"""_queries_gen.Add($"{nameof({{p.Name}}.{{prop.Name}})}={{{p.Name}}.{{prop.Name}}}")""");
                }
            }
            else
            {
                statements.Add($$"""_queries_gen.Add($"{nameof({{p.Name}})}={{{p.Name}}}")""");
            }
        }

        var setUrl = """
                     _url_gen = $"{_url_gen}?{string.Join("&", _queries_gen)}"
                     """;
        statements.Add(setUrl);
    }

    /// <summary>
    /// 处理Form参数
    /// </summary>
    /// <param name="statements"></param>
    /// <param name="paramInfos"></param>
    private static void AddFormParameters(List<Statement> statements, IEnumerable<(ParameterBindingType i, IParameterSymbol p)> paramInfos)
    {
        var formParameters = paramInfos.Where(t => t.i == ParameterBindingType.FromForm).ToList();

        if (!formParameters.Any()) return;
        statements.Add(
            "var _formData_gen = new List<global::System.Collections.Generic.KeyValuePair<string, string>>()");
        foreach (var item in formParameters)
        {
            var p = item.p;
            if (p.Type is INamedTypeSymbol
                {
                    TypeKind: TypeKind.Class, SpecialType: not SpecialType.System_String
                } parameterClassType)
            {
                var properties = parameterClassType.GetMembers().Where(m => m.Kind == SymbolKind.Property);
                foreach (var prop in properties)
                {
                    statements.Add($$"""
                                     _formData_gen.Add(new global::System.Collections.Generic.KeyValuePair<string, string>(nameof({{p.Name}}.{{prop.Name}}), $"{{{p.Name}}.{{prop.Name}}}"))
                                     """);
                }
            }
            else
            {
                statements.Add(
                    $$"""_formData_gen.Add(new global::System.Collections.Generic.KeyValuePair<string, string>(nameof({{p.Name}}), $"{{{p.Name}}}"))""");
            }
        }

        statements.Add("var _formContent_gen = new global::System.Net.Http.FormUrlEncodedContent(_formData_gen)");
        statements.Add("_formContent_gen.Headers.ContentType = new(\"application/x-www-form-urlencoded\")");
        statements.Add("_request_gen.Content = _formContent_gen");
    }

    /// <summary>
    /// 处理Body参数
    /// </summary>
    /// <param name="statements"></param>
    /// <param name="paramInfos"></param>
    /// <param name="webMethod"></param>
    /// <param name="methodSymbol"></param>
    /// <param name="errorAndWarnings"></param>
    /// <exception cref="InvalidOperationException"></exception>
    private static bool AddBodyParameters(List<Statement> statements, IEnumerable<(ParameterBindingType i, IParameterSymbol p)> paramInfos, string webMethod, IMethodSymbol methodSymbol, List<Diagnostic> errorAndWarnings)
    {
        // var bodyParameters = paramInfos.Where(t => (t.i == -1 && webMethod != WebMethodConstants.Get) || t.i == WebMethodConstants.Body).ToList();
        var bodyParameters = paramInfos.Where(t => t.i == ParameterBindingType.FromBody).ToList();
        return bodyParameters.Count switch
        {
            > 1 => DoReturnError(),
            0 => true,
            1 => DoAddBody(),
            _ => throw new ArgumentOutOfRangeException()
        };

        bool DoReturnError()
        {
            errorAndWarnings.Add(DiagnosticDefinitions.WAG00008(methodSymbol.Locations.FirstOrDefault()));
            return false;
        }

        bool DoAddBody()
        {
            var p = bodyParameters[0].p;
            statements.Add($"var _json_gen = global::System.Text.Json.JsonSerializer.Serialize({p.Name})");
            statements.Add(
                """_request_gen.Content = new global::System.Net.Http.StringContent(_json_gen, global::System.Text.Encoding.Default, "application/json")""");
            return true;
        }
    }

    /// <summary>
    /// 处理Header参数
    /// </summary>
    /// <param name="statements"></param>
    /// <param name="paramInfos"></param>
    private static bool AddHeaderParameters(List<Statement> statements, IEnumerable<(ParameterBindingType i, IParameterSymbol p)> paramInfos)
    {
        var headerParameters = paramInfos.Where(t => t.i == ParameterBindingType.FromHeader).ToList();

        if (!headerParameters.Any()) return true;
        foreach (var item in headerParameters)
        {
            var p = item.p;
            if (p.Type is INamedTypeSymbol
                {
                    TypeKind: TypeKind.Class, SpecialType: not SpecialType.System_String
                } parameterClassType)
            {
                var properties = parameterClassType.GetMembers().Where(m => m.Kind == SymbolKind.Property);
                foreach (var prop in properties)
                {
                    statements.Add(
                        $$"""_request_gen.Headers.Add(nameof({{p.Name}}.{{prop.Name}}), $"{{{p.Name}}.{{prop.Name}}}")""");
                }
            }
            else
            {
                statements.Add($$"""_request_gen.Headers.Add(nameof({{p.Name}}), $"{{{p.Name}}}")""");
            }
        }

        return true;
    }

    private static bool AddResponseHandling(List<Statement> statements, ITypeSymbol returnType,
        List<Diagnostic> errorAndWarnings, string? cancellationTokenName, IMethodSymbol methodSymbol)
    {
        cancellationTokenName ??= string.Empty;
        if (returnType is { TypeKind: TypeKind.Class, SpecialType: not SpecialType.System_String })
        {
            statements.Add($"var _stream_gen = await _response_gen.Content.ReadAsStreamAsync({cancellationTokenName})");
            statements.Add(
                $"return global::System.Text.Json.JsonSerializer.Deserialize<{returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}>(_stream_gen, _JSON_OPTIONS_gen);");
        }
        else
        {
            statements.Add($"var _str_gen = await _response_gen.Content.ReadAsStringAsync({cancellationTokenName})");
            if (returnType.SpecialType == SpecialType.System_String)
            {
                statements.Add("return _str_gen");
            }
            else if (returnType.HasTryParseMethod())
            {
                statements.Add($"{returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.TryParse(_str_gen, out var val)");
                statements.Add("return val");
            }
            else
            {
                errorAndWarnings.Add(DiagnosticDefinitions.WAG00009(methodSymbol.Locations.FirstOrDefault(), returnType.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)));
                return false;
            }
        }

        return true;
    }


    private static ConstructorBuilder BuildConstructor(INamedTypeSymbol classSymbol, bool needAuth)
    {
        List<string> parameters = ["global::System.Net.Http.IHttpClientFactory factory"];
        List<Statement> body = ["clientFactory = factory;"];
        if (needAuth)
        {
            //parameters.Add("global::AutoWasmApiGenerator.IHttpClientHeaderHandler handler");
            parameters.Add("global::System.IServiceProvider services");
            body.Add(
                "headerHandler = services.GetService<global::AutoWasmApiGenerator.IHttpClientHeaderHandler>() ?? global::AutoWasmApiGenerator.DefaultHttpClientHeaderHandler.Default");
        }

        return ConstructorBuilder.Default
            .MethodName($"{FormatClassName(classSymbol.MetadataName)}ApiInvoker")
            .AddParameter([.. parameters])
            .AddBody([.. body])
            .AddBody(
                "_JSON_OPTIONS_gen = new global::System.Text.Json.JsonSerializerOptions() { PropertyNameCaseInsensitive = true };");
    }

    private static ClassBuilder CreateHttpClassBuilder(INamedTypeSymbol interfaceSymbol)
    {
        IEnumerable<string> additionalAttribute = [];
        if (interfaceSymbol.GetAttribute(ApiInvokerGenerateAttributeFullName, out var data))
        {
            //var o = data.GetAttributeValue(nameof(ApiInvokerGeneraAttribute.Attribute));
            additionalAttribute = interfaceSymbol.GetAttributeInitInfo(ApiInvokerGenerateAttributeFullName, data!);
        }

        return ClassBuilder.Default
            .ClassName($"{FormatClassName(interfaceSymbol.MetadataName)}ApiInvoker")
            .AddGeneratedCodeAttribute(typeof(HttpServiceInvokerGenerator))
            .Attribute([.. additionalAttribute.Select(i => i.ToString())])
            .BaseType(interfaceSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }
}
