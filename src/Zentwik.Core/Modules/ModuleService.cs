using Zentwik.Contracts.Protocol;
using Zentwik.Core.Diagnostics;
using Zentwik.Core.Plans;
using Zentwik.Core.State;

namespace Zentwik.Core.Modules;

/// <summary>
/// 可选模块的启用状态。非可选模块不受开关影响——它们决定基本功能是否可用，
/// 关掉只会让人以为软件坏了。开关只用于「会改变硬件行为、需要用户主动承担风险」的模块。
/// </summary>
/// <remarks>
/// 状态存在 <see cref="StateDocument.EnabledOptionalModules"/> 里，由 <see cref="StateStore"/> 原子落盘。
/// 改动即刻生效：能力探测每次（缓存 5 秒）都会重新读取开关，不需要重启代理。
/// </remarks>
public sealed class ModuleService(StateStore store, DiagnosticLog log)
{
    public IReadOnlySet<string> EnabledOptional =>
        store.Current.EnabledOptionalModules.ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>非可选模块恒为启用；未知模块视为未启用。</summary>
    public bool IsEnabled(string moduleId)
    {
        var definition = ModuleCatalog.Find(moduleId);
        if (definition is null)
        {
            return false;
        }

        return !definition.Optional || EnabledOptional.Contains(definition.Id);
    }

    public async Task<IReadOnlySet<string>> EnableAsync(string moduleId, CancellationToken ct = default)
    {
        var definition = RequireOptional(moduleId);
        if (IsEnabled(definition.Id))
        {
            return EnabledOptional;
        }

        await store.UpdateAsync(
            document => document with
            {
                EnabledOptionalModules = [.. document.EnabledOptionalModules, definition.Id],
            },
            ct).ConfigureAwait(false);

        log.Info($"已启用可选模块 {definition.Id}（{definition.Name}）。");
        return EnabledOptional;
    }

    public async Task<IReadOnlySet<string>> DisableAsync(string moduleId, CancellationToken ct = default)
    {
        var definition = RequireOptional(moduleId);
        if (!IsEnabled(definition.Id))
        {
            return EnabledOptional;
        }

        await store.UpdateAsync(
            document => document with
            {
                EnabledOptionalModules = document.EnabledOptionalModules
                    .Where(id => !string.Equals(id, definition.Id, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
            },
            ct).ConfigureAwait(false);

        log.Info($"已停用可选模块 {definition.Id}（{definition.Name}）。");
        return EnabledOptional;
    }

    private ModuleDefinition RequireOptional(string moduleId)
    {
        var definition = ModuleCatalog.Find(moduleId);
        if (definition is null)
        {
            throw new CoreException(ErrorCodes.ModuleNotFound, $"未知模块：{moduleId}。可用模块见 modules 命令。");
        }

        if (!definition.Optional)
        {
            throw new CoreException(
                ErrorCodes.ModuleNotOptional,
                $"模块 {definition.Id}（{definition.Name}）是内置组件，始终启用，不能单独关闭。");
        }

        return definition;
    }
}
