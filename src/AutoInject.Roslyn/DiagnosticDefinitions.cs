﻿using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Text;

namespace AutoInjectGenerator
{
    internal class DiagnosticDefinitions
    {
        /// <summary>
        /// 未提供一个公开的静态的分部方法( public static partial )
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        public static Diagnostic AIG00001(Location? location) => Diagnostic.Create(new DiagnosticDescriptor(
                        id: "AIG00001",
                        title: "未提供一个公开的静态的分部方法( public static partial )",
                        messageFormat: "未提供一个公开的静态的分部方法( public static partial )",
                        category: typeof(AutoInjectContextGenerator).FullName!,
                        defaultSeverity: DiagnosticSeverity.Error,
                        isEnabledByDefault: true), location);

        /// <summary>
        /// 配置冲突，Include和Exclude包含相同的值
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        public static Diagnostic AIG00002(Location? location) => Diagnostic.Create(new DiagnosticDescriptor(
                        id: "AIG00002",
                        title: "配置冲突，Include和Exclude包含相同的值",
                        messageFormat: "配置冲突，Include和Exclude包含相同的值",
                        category: typeof(AutoInjectContextGenerator).FullName!,
                        defaultSeverity: DiagnosticSeverity.Error,
                        isEnabledByDefault: true), location);
        /// <summary>
        /// 
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        public static Diagnostic AIG00003(Location? location) => Diagnostic.Create(new DiagnosticDescriptor(
                        id: "AIG00003",
                        title: "该类型不能作为该接口的实现类型",
                        messageFormat: "该类型不能作为该接口的实现类型",
                        category: typeof(AutoInjectContextGenerator).FullName!,
                        defaultSeverity: DiagnosticSeverity.Error,
                        isEnabledByDefault: true), location);
    }
}
