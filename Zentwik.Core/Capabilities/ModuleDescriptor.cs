using System;
using System.Collections.Generic;

namespace Zentwik.Core.Capabilities
{
    /// <summary>
    /// 一个模块声明自己提供哪些能力。<see cref="Mask"/> 与模块状态按位与：
    /// 模块整体可用、但某项能力依赖的条件不具备时（例如 Intel 模块不提供方案保存），
    /// 用它把该能力的某一位单独置空。默认 <see cref="All"/> 表示完全跟随模块。
    /// </summary>
    public readonly struct CapabilityContribution
    {
        public CapabilityContribution(string capability, CapabilityState mask = CapabilityStateExtensions.All, string? reason = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(capability);
            Capability = capability;
            Mask = mask;
            Reason = reason;
        }

        public string Capability { get; }

        public CapabilityState Mask { get; }

        /// <summary>该能力单独的原因说明；null 表示沿用模块的原因。</summary>
        public string? Reason { get; }
    }

    /// <summary>
    /// 模块描述。不含任何厂商实现，只描述「是谁、依赖谁、提供什么、现在什么状态」。
    /// </summary>
    public sealed record ModuleDescriptor
    {
        public required string Id { get; init; }

        /// <summary>界面可读名；为空时界面显示 <see cref="Id"/>。</summary>
        public string DisplayName { get; init; } = string.Empty;

        public Version Version { get; init; } = new(1, 0);

        /// <summary>依赖的模块 Id。依赖缺失时该模块不注册、不启动。</summary>
        public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();

        /// <summary>提供的能力。<see cref="CapabilityCatalog"/> 由它派生能力快照。</summary>
        public IReadOnlyList<CapabilityContribution> Provides { get; init; } = Array.Empty<CapabilityContribution>();

        /// <summary>当前状态。由目录在注册与启动过程中更新。</summary>
        public ModuleState State { get; init; } = ModuleStateExtensions.BuiltIn;

        /// <summary>状态原因：不可用、被禁用或初始化失败的具体说明。</summary>
        public string? Reason { get; init; }
    }
}
