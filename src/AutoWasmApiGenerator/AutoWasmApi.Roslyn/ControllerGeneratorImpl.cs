﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Generators.Shared;
using Generators.Shared.Builder;
using Microsoft.CodeAnalysis;

namespace AutoWasmApiGenerator;

public class ControllerGeneratorImpl : IControllerGenerator
{
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
        var methods = interfaceSymbol.GetAllMethodWithAttribute(GeneratorHelpers.WebMethodAttributeFullName).ToArray();
        if (methods.Any(a => a.Symbol.IsGenericMethod) || interfaceSymbol.IsGenericType)
        {
            file = null;
            errorAndWarnings.Add(DiagnosticDefinitions.WAG00004(Location.None)); // TODO: Report error location
            return false;
        }
        var ns = NamespaceBuilder.Default.Namespace(interfaceSymbol.ContainingNamespace.ToDisplayString());
        var controllerClass = CreateControllerClass(interfaceSymbol, out var attributeData);
        List<Node> members = [];
        var localField = BuildLocalField(interfaceSymbol);
        var constructor = BuildConstructor(interfaceSymbol);
        members.Add(localField);
        members.Add(constructor);
        var needAuth = attributeData.GetNamedValue("Authorize") ?? false;
        foreach (var methodSymbol in methods)
        {
            if (methodSymbol.Symbol.HasAttribute(GeneratorHelpers.NotSupported))
            {
                continue;
            }
            var httpMethod = TryGetHttpMethod(methodSymbol);
            var methodSyntax = BuildMethod(methodSymbol, httpMethod, (bool)needAuth);
            members.Add(methodSyntax);
        }
        file = CodeFile.New($"{interfaceSymbol.FormatFileName()}Controller.g.cs")
            .AddMembers(ns.AddMembers(controllerClass.AddMembers([.. members])));
        //.AddUsings(source.GetTargetUsings());

        return true;
    }

    private static MethodBuilder BuildMethod((IMethodSymbol, AttributeData?) data, string httpMethod, bool needAuth)
    {
        /*
         * [global::Microsoft.AspNetCore.Mvc.{httpMethod}("...")]
         * [global::System.CodeDom.Compiler.GeneratedCode("...", "...")]
         * public <RETURN_TYPE> <METHOD_NAME>(<Parameter> p) => proxyService.<METHOD_NAME>(p);
         */
        var a = data.Item2;
        var methodSymbol = data.Item1;
        var methodScoped = methodSymbol.Name.Replace("Async", "");
        var customRoute = a?.GetNamedValue("Route")?.ToString();
        string methodRoute;
        if (string.IsNullOrEmpty(customRoute))
        {
            methodRoute = methodScoped;
        }
        else if (Regex.Match(customRoute, "{.+}").Success)
        {
            methodRoute = $"{methodScoped}/{customRoute}";
        }
        else
        {
            methodRoute = customRoute!;
        }

        var methodRouteAttribute =
            $"global::Microsoft.AspNetCore.Mvc.Http{httpMethod}(\"{methodRoute}\")";
        var allowAnonymous = (bool)(a?.GetNamedValue("AllowAnonymous") ?? false);
        var methodAuth = (bool)(a?.GetNamedValue("Authorize") ?? false);
        return MethodBuilder.Default
            .MethodName(methodSymbol.Name)
            .ReturnType(methodSymbol.ReturnType.ToDisplayString())
            .Attribute(methodRouteAttribute)
            .AttributeIf(allowAnonymous, "global::Microsoft.AspNetCore.Authorization.AllowAnonymous")
            .AttributeIf((methodAuth || needAuth) && !allowAnonymous,
                "global::Microsoft.AspNetCore.Authorization.Authorize")
            .AddGeneratedCodeAttribute(typeof(ControllerGenerator))
            .AddParameter(GenerateParameter(httpMethod, methodSymbol))
            .Lambda(
                $"proxyService.{methodSymbol.Name}({string.Join(", ", methodSymbol.Parameters.Select(p => p.Name))});");
    }

    private static string[] GenerateParameter(string httpMethod, IMethodSymbol methodSymbol)
    {
        var parametersBinding = methodSymbol.Parameters.Select(p =>
        {
            // 忽略CancellationToken
            if (p.Type.ToString() == typeof(CancellationToken).FullName)
            {
                return (bindingType: ParameterBindingType.Ignore, p);
            }

            if (!p.GetAttribute(GeneratorHelpers.WebMethodParameterBindingAttribute, out var binding))
            {
                return (bindingType: ParameterBindingType.Fallback, p);
            }

            Debug.Assert(binding != null, nameof(binding) + " != null");
            if (!binding!.GetConstructorValue(0, out var bindingType))
            {
                return (bindingType: ParameterBindingType.Ignore, p);
            }

            return (bindingType: (ParameterBindingType)(int)bindingType!, p);
        }).ToList();

        var enumerable = parametersBinding.Select(b =>
        {
            var (bindingType, p) = b;
            return $"{CreateMethodParameterBinding(bindingType, httpMethod)}{p.Type.ToDisplayString()} {p.Name}";
        });
        return enumerable.ToArray();

        static string CreateMethodParameterOriginAttribute(string method)
        {
            return method switch
            {
                "Get" => "[global::Microsoft.AspNetCore.Mvc.FromQuery]",
                "Post" => "[global::Microsoft.AspNetCore.Mvc.FromBody]",
                "Put" => "[global::Microsoft.AspNetCore.Mvc.FromBody]",
                "Delete" => "[global::Microsoft.AspNetCore.Mvc.FromBody]",
                _ => throw new NotImplementedException()
            };
        }

        static string CreateMethodParameterBinding(ParameterBindingType bindingType, string method)
        {
            return bindingType switch
            {
                ParameterBindingType.FromQuery => "[global::Microsoft.AspNetCore.Mvc.FromQuery]", // FromQuery
                ParameterBindingType.FromRoute => "[global::Microsoft.AspNetCore.Mvc.FromRoute]", // FromRoute
                ParameterBindingType.FromForm => "[global::Microsoft.AspNetCore.Mvc.FromForm]", // FromForm
                ParameterBindingType.FromBody => "[global::Microsoft.AspNetCore.Mvc.FromBody]", // FromBody
                ParameterBindingType.FromHeader => "[global::Microsoft.AspNetCore.Mvc.FromHeader]", // FromHeader
                ParameterBindingType.FromServices => "[global::Microsoft.AspNetCore.Mvc.FromServices]", // FromServices
                ParameterBindingType.Ignore => "", // Ignore
                _ => CreateMethodParameterOriginAttribute(method), // Fall back to default
            };
        }
    }

    private static FieldBuilder BuildLocalField(INamedTypeSymbol interfaceSymbol)
    {
        // private readonly <SERVICE_TYPE> proxyService;
        //return FieldDeclaration(VariableDeclaration(IdentifierName(interfaceSymbol.ToDisplayString())).AddVariables(VariableDeclarator(Identifier("proxyService")))).AddModifiers(Token(SyntaxKind.PrivateKeyword), Token(SyntaxKind.ReadOnlyKeyword));
        return FieldBuilder.Default
            .MemberType(interfaceSymbol.ToDisplayString())
            .FieldName("proxyService");
    }

    private static ConstructorBuilder BuildConstructor(INamedTypeSymbol interfaceSymbol)
    {
        /*
         * public <SERVICE_TYPE>Controller(<SERVICE_TYPE> service)
         * {
         *     proxyService = service;
         * }
         */

        return ConstructorBuilder.Default
            .MethodName($"{GeneratorHelpers.FormatClassName(interfaceSymbol.FormatClassName())}Controller")
            .AddGeneratedCodeAttribute(typeof(ControllerGenerator))
            .AddBody("proxyService = service;")
            .AddParameter($"{interfaceSymbol.ToDisplayString()} service");
    }

    private static ClassBuilder CreateControllerClass(INamedTypeSymbol interfaceSymbol, out AttributeData? controllerAttribute)
    {
        _ = interfaceSymbol.GetAttribute(GeneratorHelpers.WebControllerAttributeFullName, out controllerAttribute);
        var route = controllerAttribute.GetNamedValue("Route") ?? "[controller]";
        var needAuth = controllerAttribute.GetNamedValue("Authorize") ?? false;
        //var additionalAttribute = source.TargetSymbol.GetAttributeInitInfo<ControllerGenerator>();

        return ClassBuilder.Default
            .ClassName($"{GeneratorHelpers.FormatClassName(interfaceSymbol.FormatClassName())}Controller")
            .Modifiers("public")
            .BaseType("global::Microsoft.AspNetCore.Mvc.ControllerBase")
            .Attribute("global::Microsoft.AspNetCore.Mvc.ApiController")
            .Attribute($"global::Microsoft.AspNetCore.Mvc.Route(\"api/{route}\")")
            .AttributeIf((bool)needAuth, "global::Microsoft.AspNetCore.Authorization.Authorize")
            //.Attribute([..additionalAttribute.Select(i => i.ToString())])
            .AddGeneratedCodeAttribute(typeof(ControllerGenerator));
    }

    private static string TryGetHttpMethod((IMethodSymbol symbol, AttributeData? attributeData) data)
    {
        if (data.attributeData?.GetNamedValue("Method", out var m) == true)
        {
            return GeneratorHelpers.WebMethod[(int)m!];
        }

        //没有指定Method，就默认Post
        return GeneratorHelpers.WebMethod[1];

        //var symbol = data.Item1;
        ////if (symbol.Parameters.Any(p =>p.Type.isr))

        //var name = symbol.Name;
        //if (name.StartsWith("create", StringComparison.OrdinalIgnoreCase)
        //    || name.StartsWith("add", StringComparison.OrdinalIgnoreCase))
        //{
        //    return WebMethod[1];
        //}
        //else if (name.StartsWith("get", StringComparison.OrdinalIgnoreCase)
        //    || name.StartsWith("find", StringComparison.OrdinalIgnoreCase)
        //    || name.StartsWith("query", StringComparison.OrdinalIgnoreCase))
        //{
        //    return WebMethod[0];
        //}
        //else if (name.StartsWith("update", StringComparison.OrdinalIgnoreCase)
        //    || name.StartsWith("put", StringComparison.OrdinalIgnoreCase))
        //{
        //    return WebMethod[2];
        //}
        //else if (name.StartsWith("delete", StringComparison.OrdinalIgnoreCase)
        //    || name.StartsWith("remove", StringComparison.OrdinalIgnoreCase))
        //{
        //    return WebMethod[3];
        //}
        //else
        //{
        //    return WebMethod[1];
        //}
    }
}