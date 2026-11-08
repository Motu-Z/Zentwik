using System;
using System.Collections.Generic;

namespace Zentwik.Core.Services
{
    /// <summary>
    /// 电源策略的读写服务：范围校验、错误聚合、快照组装。
    ///
    /// 【为什么这一层不直接 P/Invoke】
    /// 这一层要判的是「值合不合法」「这一项本机支不支持」「失败了该怎么措辞」——
    /// 全是纯逻辑，全部可以用假数据驱动。真正碰系统的只有
    /// <see cref="IPowerPolicyApi"/> 的五个方法。把两者分开之后，
    /// 「主板不支持 Boost 4」这种在开发机上不一定复现得出来的分支也能被测试钉住。
    ///
    /// 【为什么读的是 AC 值】
    /// 每项设置都有 AC（交流电）与 DC（电池）两份值。本工具面向的是插着电的台式机，
    /// 界面上的滑块也只表达「现在这一套」；同时读写两份会让「我改了但没生效」
    /// 变成常态（用户拔了电就换了另一份）。DC 值不在本批范围内。
    ///
    /// 【写入之后为什么必须重新激活方案】
    /// <c>PowerWriteACValueIndex</c> 只改方案里的存储值，系统不会立刻按新值调度。
    /// 必须对同一个方案再调一次 <c>PowerSetActiveScheme</c>，让电源管理器重新加载。
    /// 少了这一步，用户会看到「界面显示已改、实际功耗曲线没变」——
    /// 而且重启后才生效，看起来像程序没保存。
    /// </summary>
    public sealed class PowerPolicyService
    {
        // ---------------- 设置项 GUID ----------------
        // 这些不是随手抄来的常量：已在本机注册表
        // HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings 下逐项核对过
        // （子组目录 → 设置项目录 → FriendlyName）。
        //
        // 【为什么写死在代码里而不是运行时枚举】
        // 注册表里那份清单只包含「本机暴露的」设置项，而且每台机器不一样。
        // 按名字模糊匹配会在中英文系统、不同主板 BIOS 上匹到不同的项 ——
        // 一个「最大处理器状态」被读成「最小处理器状态」的 bug，从功耗上看不出来。
        // GUID 是稳定的契约，名字只是给人看的。

        /// <summary>「处理器电源管理」子组。</summary>
        public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

        /// <summary>「PCI Express」子组。</summary>
        public static readonly Guid SubPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");

        /// <summary>EPP：Processor energy performance preference policy，0-100。</summary>
        public static readonly Guid SettingEpp = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

        /// <summary>Processor performance boost mode，0-4。</summary>
        public static readonly Guid SettingBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

        /// <summary>Maximum processor state，0-100（本工具只开放 50-100）。</summary>
        public static readonly Guid SettingMaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

        /// <summary>Processor idle disable：1 = 禁用空闲（关 C-State），0 = 允许空闲。</summary>
        public static readonly Guid SettingIdleDisable = new("5d76a2ca-e8c0-402f-a133-2158492d58ad");

        /// <summary>Link State Power Management（ASPM），0-2。</summary>
        public static readonly Guid SettingAspm = new("ee12f906-d277-404b-b6da-e5fa1a576df5");

        // ---------------- 固定文案 ----------------
        // 三句话分属三个不同的失败点，措辞刻意不同：
        //   · 读不到活动方案 → 说明「拿不到对象」，五项都无从谈起；
        //   · 写不进去       → 说明「拿不到对象」，但这次是写操作；
        //   · 激活失败       → 说明「值已经写进去了，但没生效」，用户该做的是重启而不是重试。

        /// <summary>读 / 写时取不到活动方案。</summary>
        public const string NoActiveSchemeMessage = "无法读取当前电源方案";

        /// <summary>
        /// 快照降级时每项携带的原因。
        ///
        /// 与 <see cref="NoActiveSchemeMessage"/> 是同一件事的两种说法：
        /// 一个是「本次操作失败」的结果文案，一个是「这一项为什么读不到」的
        /// 逐项说明。合成一句会让快照在界面上显示成「读取失败」，
        /// 而它其实是一次成功的读取 —— 只是这台机器当前没有可用的方案。
        /// </summary>
        public const string NoActiveSchemeForSnapshotMessage = "无法获取当前电源方案";

        /// <summary>值已写入但重新激活失败。</summary>
        public const string ActivateFailedMessage = "激活失败，请重启系统后生效";

        /// <summary><c>apply-power-policy</c> 收到一份什么都没填的请求。</summary>
        public const string EmptyPayloadMessage = "没有要修改的字段";

        /// <summary>切换方案时给了空 GUID。</summary>
        public const string EmptySchemeIdMessage = "方案 Id 为空";

        /// <summary>要切换的方案不在本机方案列表里。</summary>
        public const string SchemeNotFoundMessage = "方案不存在";

        /// <summary>方案名为空。</summary>
        public const string EmptySchemeNameMessage = "方案名不能为空";

        /// <summary>方案名超长。</summary>
        public const string SchemeNameTooLongMessage = "方案名不能超过 24 个字符";

        /// <summary>本机已有同名方案。</summary>
        public const string SchemeNameExistsMessage = "已存在同名方案";

        /// <summary>复制方案本身失败。</summary>
        public const string CreateSchemeFailedMessage = "无法创建电源方案";

        /// <summary>复制成功但改名失败（此时新建的那份已被回滚删除）。</summary>
        public const string RenameSchemeFailedMessage = "方案改名失败";

        /// <summary>删除方案的 Win32 调用失败。</summary>
        public const string DeleteSchemeFailedMessage = "删除方案失败";

        /// <summary>试图删除 Windows 内置方案。</summary>
        public const string BuiltInSchemeProtectedMessage = "内置方案不能删除";

        /// <summary>派生用的模板既不是内置预设、也不在本机方案列表里。</summary>
        public const string TemplateNotFoundMessage = "模板方案不存在";

        /// <summary>方案名长度上限。</summary>
        public const int MaxSchemeNameLength = 24;

        // ---------------- 参数名（只给错误文案用） ----------------
        // 【这些是专业术语，不是界面文案】
        // 它们只出现在 IPC 的 Message 与错误日志里，供开发者与排障时定位。
        // 面向普通用户的界面**必须**另写通俗解释（副标题 / Tooltip），
        // 不得把「ASPM」「EPP」这类裸术语直接摆在界面上 —— 详见契约文档末尾的备注。
        //
        // 【为什么要带括号里的英文缩写】
        // 中文名单独出现时无法与 `powercfg /q` 的输出、以及各种主板文档对上；
        // 括号里的缩写是这些项在系统与硬件资料里的唯一通用标识。

        /// <summary>EPP 的参数名。</summary>
        public const string LabelEpp = "能效偏好（EPP）";

        /// <summary>Boost 的参数名。</summary>
        public const string LabelBoostMode = "升频模式（Boost）";

        /// <summary>最大处理器状态的参数名。</summary>
        public const string LabelMaxProcessorState = "最大处理器状态";

        /// <summary>C-State 总开关的参数名。</summary>
        public const string LabelIdleDisable = "空闲禁用（C-State）";

        /// <summary>ASPM 的参数名。</summary>
        public const string LabelAspm = "ASPM";

        private readonly IPowerPolicyApi _api;

        public PowerPolicyService(IPowerPolicyApi api)
        {
            ArgumentNullException.ThrowIfNull(api);
            _api = api;
        }

        // ==================== 读 ====================

        /// <summary>
        /// 读一份完整快照。**永远不返回 null**：取不到活动方案时返回
        /// <see cref="PowerPolicySnapshot.Unavailable"/> 造的降级实例（见该方法的注释）。
        /// </summary>
        public PowerPolicySnapshot ReadSnapshot()
        {
            Guid? active = _api.GetActiveScheme();
            if (active is null)
                return PowerPolicySnapshot.Unavailable(NoActiveSchemeForSnapshotMessage);

            Guid scheme = active.Value;
            string? name = _api.ReadFriendlyName(scheme);
            (bool hasBattery, bool? onAc) = _api.ReadPowerSupply();

            PowerPolicySnapshot snapshot = new()
            {
                ActiveSchemeGuid = scheme,
                ActiveSchemeName = string.IsNullOrWhiteSpace(name)
                    ? PowerPolicySnapshot.UnknownSchemeName
                    : name!,
                HasBattery = hasBattery,
                OnAcPower = onAc,
            };

            (snapshot.EppSupported, snapshot.Epp, snapshot.EppError) =
                ReadIndex(scheme, SubProcessor, SettingEpp, dc: false, LabelEpp);

            (snapshot.BoostModeSupported, snapshot.BoostMode, snapshot.BoostModeError) =
                ReadIndex(scheme, SubProcessor, SettingBoostMode, dc: false, LabelBoostMode);

            (snapshot.MaxProcessorStateSupported, snapshot.MaxProcessorState, snapshot.MaxProcessorStateError) =
                ReadIndex(scheme, SubProcessor, SettingMaxProcessorState, dc: false, LabelMaxProcessorState);

            (snapshot.AspmSupported, snapshot.Aspm, snapshot.AspmError) =
                ReadIndex(scheme, SubPciExpress, SettingAspm, dc: false, LabelAspm);

            ReadIdleDisable(scheme, dc: false, out bool acIdleSupported, out bool? acIdle, out string? acIdleError);
            snapshot.IdleDisableSupported = acIdleSupported;
            snapshot.IdleDisable = acIdle;
            snapshot.IdleDisableError = acIdleError;

            if (!snapshot.HasBattery)
                return snapshot;

            (snapshot.DcEppSupported, snapshot.DcEpp, snapshot.DcEppError) =
                ReadIndex(scheme, SubProcessor, SettingEpp, dc: true, LabelEpp);

            (snapshot.DcBoostModeSupported, snapshot.DcBoostMode, snapshot.DcBoostModeError) =
                ReadIndex(scheme, SubProcessor, SettingBoostMode, dc: true, LabelBoostMode);

            (snapshot.DcMaxProcessorStateSupported, snapshot.DcMaxProcessorState, snapshot.DcMaxProcessorStateError) =
                ReadIndex(scheme, SubProcessor, SettingMaxProcessorState, dc: true, LabelMaxProcessorState);

            (snapshot.DcAspmSupported, snapshot.DcAspm, snapshot.DcAspmError) =
                ReadIndex(scheme, SubPciExpress, SettingAspm, dc: true, LabelAspm);

            ReadIdleDisable(scheme, dc: true, out bool dcIdleSupported, out bool? dcIdle, out string? dcIdleError);
            snapshot.DcIdleDisableSupported = dcIdleSupported;
            snapshot.DcIdleDisable = dcIdle;
            snapshot.DcIdleDisableError = dcIdleError;

            return snapshot;
        }

        /// <summary>
        /// 读一项，把「支持与否」与「错误原因」一起回出去。
        /// </summary>
        private (bool Supported, int? Value, string? Error) ReadIndex(
            Guid scheme, Guid subgroup, Guid setting, bool dc, string label)
        {
            (uint result, uint value) = dc
                ? _api.ReadDcValueIndex(scheme, subgroup, setting)
                : _api.ReadAcValueIndex(scheme, subgroup, setting);

            return result == 0
                ? (true, (int)value, null)
                : (false, null, DescribeReadFailure(label, result));
        }

        /// <summary>
        /// C-State 是唯一以布尔表达的设置项，单独走一条。
        ///
        /// 系统里存的是 1 = 禁用空闲。非 0 一律当「已禁用」：这一项只有 0 / 1
        /// 两个合法值，出现别的值说明读到的不是我们要的东西，但把它当「允许空闲」
        /// 会掩盖问题，当「已禁用」至少与「非 0」字面一致。
        /// </summary>
        private void ReadIdleDisable(
            Guid scheme, bool dc, out bool supported, out bool? value, out string? error)
        {
            (uint result, uint raw) = dc
                ? _api.ReadDcValueIndex(scheme, SubProcessor, SettingIdleDisable)
                : _api.ReadAcValueIndex(scheme, SubProcessor, SettingIdleDisable);

            supported = result == 0;
            value = supported ? raw != 0 : null;
            error = supported ? null : DescribeReadFailure(LabelIdleDisable, result);
        }

        // ==================== 写 ====================

        /// <summary>
        /// EPP，0-100。超出范围直接拒绝，不写系统。
        /// <paramref name="dc"/> 为 true 时写电池那一套。
        /// </summary>
        public (bool Success, string? Error) WriteEpp(int value, bool dc = false)
        {
            string label = ScopeLabel(LabelEpp, dc);

            if (value < 0 || value > 100)
                return (false, DescribeOutOfRange(label, 0, 100));

            return WriteCore(SubProcessor, SettingEpp, (uint)value, label, dc);
        }

        /// <summary>
        /// Boost 模式，0-4。
        ///
        /// 【0-4 是「我们愿意下发的范围」，不是「系统保证接受的范围」】
        /// 部分主板只认 0 / 1，写入 2-4 时 <c>PowerWriteACValueIndex</c> 会返回非 0。
        /// 那个错误码必须原样带出去（见 <see cref="WriteCore"/>），
        /// 不能在本地提前判死 —— 同型号主板不同 BIOS 版本的支持面就不一样。
        /// </summary>
        public (bool Success, string? Error) WriteBoostMode(int value, bool dc = false)
        {
            string label = ScopeLabel(LabelBoostMode, dc);

            if (value < 0 || value > 4)
                return (false, DescribeOutOfRange(label, 0, 4));

            return WriteCore(SubProcessor, SettingBoostMode, (uint)value, label, dc);
        }

        /// <summary>
        /// 最大处理器状态，50-100。
        ///
        /// 【为什么下限是 50 而不是系统的 0】
        /// 系统的合法区间是 0-100，但把它设到 50 以下是「让 CPU 跑在基频的一半」——
        /// 用户点这个滑块的目的是省电或降温，不是把机器变成上网本。
        /// 真设到 5% 的后果是整机卡到连界面都拖不动，而恢复要靠另一台机器改电源方案。
        /// </summary>
        public (bool Success, string? Error) WriteMaxProcessorState(int value, bool dc = false)
        {
            string label = ScopeLabel(LabelMaxProcessorState, dc);

            if (value < 50 || value > 100)
                return (false, DescribeOutOfRange(label, 50, 100));

            return WriteCore(SubProcessor, SettingMaxProcessorState, (uint)value, label, dc);
        }

        /// <summary>ASPM，0-2（0 关闭 / 1 中等节能 / 2 最大节能）。</summary>
        public (bool Success, string? Error) WriteAspm(int value, bool dc = false)
        {
            string label = ScopeLabel(LabelAspm, dc);

            if (value < 0 || value > 2)
                return (false, DescribeOutOfRange(label, 0, 2));

            return WriteCore(SubPciExpress, SettingAspm, (uint)value, label, dc);
        }

        /// <summary>
        /// C-State 总开关。<b>true（禁用空闲）写 1，false（允许空闲）写 0</b>。
        ///
        /// 【为什么不叫 EnableCState】
        /// 系统里这一项的名字是「Processor idle disable」，值 1 表示**禁用**。
        /// 接口照抄系统语义（idleDisable = true 就是禁用），而不是翻成「启用」——
        /// 一旦翻转，调用方每次读这段代码都要在脑子里做一次取反，
        /// 而取反错一次就是「想让 CPU 降频却把它锁在了最高频」。
        /// </summary>
        public (bool Success, string? Error) WriteIdleDisable(bool disabled, bool dc = false)
            => WriteCore(
                SubProcessor,
                SettingIdleDisable,
                disabled ? 1u : 0u,
                ScopeLabel(LabelIdleDisable, dc),
                dc);

        /// <summary>
        /// 区分 AC / DC 的错误文案前缀。
        ///
        /// 不加前缀时，电池那一套写失败会报「能效偏好（EPP）写入失败」——
        /// 用户当前停在电池分段上，会以为这是插电那套的值出了问题。
        /// </summary>
        private static string ScopeLabel(string label, bool dc)
            => dc ? $"电池{label}" : label;

        /// <summary>
        /// 重新激活当前方案，让刚写进去的值真正生效。
        ///
        /// 【为什么先读一次再写回同一个 GUID】
        /// <c>PowerSetActiveScheme</c> 需要方案 GUID，而本类不缓存它 ——
        /// 用户可能在这期间用系统设置面板换了方案。读一次再激活，
        /// 保证激活的永远是「现在生效的那个」，而不是启动时缓存的那个。
        /// </summary>
        public (bool Success, string? Error) RefreshActiveScheme()
        {
            Guid? active = _api.GetActiveScheme();
            if (active is null)
                return (false, NoActiveSchemeMessage);

            return _api.SetActiveScheme(active.Value)
                ? (true, null)
                : (false, ActivateFailedMessage);
        }

        /// <summary>当前活动方案 GUID。取不到时返回 <see cref="Guid.Empty"/>。</summary>
        public Guid GetActiveScheme()
            => _api.GetActiveScheme() ?? Guid.Empty;

        /// <summary>
        /// 本机全部电源方案。名字读不到的用「未知」占位。
        /// </summary>
        public IReadOnlyList<(Guid Guid, string Name)> ListSchemes()
        {
            List<(Guid Guid, string Name)> result = new();

            foreach ((Guid guid, string? name) in _api.EnumerateSchemes())
            {
                result.Add((
                    guid,
                    string.IsNullOrWhiteSpace(name) ? PowerPolicySnapshot.UnknownSchemeName : name));
            }

            return result;
        }

        /// <summary>
        /// 切换活动方案。
        ///
        /// 先核对方案确实存在：<c>PowerSetActiveScheme</c> 对不存在的 GUID
        /// 在某些系统上返回 0（成功），方案却没有任何变化 —— 界面会显示
        /// 「已切换」而实际没动。自己先列一遍能拦住这种静默失败。
        /// </summary>
        public (bool Success, string? Error) SetActiveScheme(Guid guid)
        {
            if (guid == Guid.Empty)
                return (false, EmptySchemeIdMessage);

            if (!SchemeExists(guid))
                return (false, SchemeNotFoundMessage);

            return _api.SetActiveScheme(guid)
                ? (true, null)
                : (false, ActivateFailedMessage);
        }

        /// <summary>
        /// 以某个模板派生一份新方案，改名为 <paramref name="name"/>。
        /// 返回新方案的 GUID；失败时 <c>Success = false</c> 且 <c>Created</c> 为 <see cref="Guid.Empty"/>。
        /// </summary>
        /// <remarks>
        /// 【模板可以是内置预设，也可以是本机现有方案】
        /// 内置预设即使没出现在方案列表里也能派生（模板键一直在注册表里），
        /// 因此在 <see cref="SchemeExists"/> 之前先判 <see cref="PowerSchemePresets.IsBuiltIn"/>。
        ///
        /// 【派生源固定用「平衡」，模板只当取值来源】
        /// 从节能 / 高性能 / 卓越性能派生出来的方案会继承模板的隐藏标记：
        /// 它不出现在 <c>PowerEnumerate</c> 的结果里，只在被激活的那一刻临时可见，
        /// 一旦切到别的方案就从界面上消失 —— 用户看到的就是「新建的方案被删了」。
        /// 微软自家的 <c>powercfg -duplicatescheme</c> 同样如此，不是我们的用法问题，
        /// 而隐藏标记在注册表里由 SYSTEM 独占写入，管理员也改不了。
        /// 所以改成「从平衡派生（始终可见）+ 把模板的全部设置项搬过去」。
        ///
        /// 【改名失败必须回滚】
        /// 复制出来的方案继承模板的名字。改名失败却把它留下，用户会看到两张同名卡片，
        /// 而且下次再点「新建」只会再多一张。删掉它比留一个分不清是谁的方案干净。
        ///
        /// 【说明文字与设置一样要搬】
        /// 派生品继承的是「平衡」的说明，不搬就出现「卓越性能 —— 自动平衡功耗与性能」。
        /// </remarks>
        public (bool Success, Guid Created, string? Error) CreateScheme(Guid templateGuid, string name)
        {
            (string? trimmed, string? nameError) = NormalizeSchemeName(name);
            if (trimmed is null)
                return (false, Guid.Empty, nameError);

            if (!PowerSchemePresets.IsBuiltIn(templateGuid) && !SchemeExists(templateGuid))
                return (false, Guid.Empty, TemplateNotFoundMessage);

            if (NameExists(trimmed, Guid.Empty))
                return (false, Guid.Empty, SchemeNameExistsMessage);

            Guid? created = _api.DuplicateScheme(PowerSchemePresets.Balanced.Guid);
            if (created is null)
                return (false, Guid.Empty, CreateSchemeFailedMessage);

            // 卓越性能在微软的模板体系里是「高性能之上再压一层」：本机的模板键只显式写了
            // 硬盘与 PCIe 两项，单独搬它等于复制平衡。先铺高性能的底，再让模板自己的取值覆盖。
            if (templateGuid == PowerSchemePresets.UltimatePerformance.Guid)
                CopySettings(PowerSchemePresets.HighPerformance.Guid, created.Value);

            CopySettings(templateGuid, created.Value);

            if (!_api.RenameScheme(created.Value, trimmed))
            {
                _api.DeleteScheme(created.Value);
                return (false, Guid.Empty, RenameSchemeFailedMessage);
            }

            CopyDescription(templateGuid, created.Value);

            return (true, created.Value, null);
        }

        /// <summary>
        /// 把模板的说明文字搬到新方案上。
        /// </summary>
        /// <remarks>
        /// 说明文字随派生走，不搬的话「卓越性能」下面写着「自动平衡功耗与性能」——
        /// 与名字自相矛盾。失败不阻断：非提权进程写不了 HKLM，
        /// 那也不该让方案建不出来。
        /// </remarks>
        private void CopyDescription(Guid source, Guid target)
        {
            string? description = _api.ReadSchemeDescription(source);
            if (!string.IsNullOrWhiteSpace(description))
                _api.WriteSchemeDescription(target, description);
        }

        /// <summary>
        /// 把一个方案的全部设置项取值复制到另一个方案（AC 与 DC 各一套）。
        /// </summary>
        /// <remarks>
        /// 单项失败直接跳过：部分设置项在目标方案上不接受写入（受硬件或驱动限制），
        /// 为一项失败放弃整次新建，用户会连方案都建不出来。本页能改的五项
        /// 一定在其中，漏掉的只会是那些本来就改不动的项。
        /// </remarks>
        private void CopySettings(Guid source, Guid target)
        {
            if (source == target)
                return;

            foreach (Guid subgroup in _api.EnumerateSubgroups(source))
            {
                foreach (Guid setting in _api.EnumerateSettings(source, subgroup))
                {
                    (uint acResult, uint acValue) = _api.ReadAcValueIndex(source, subgroup, setting);
                    if (acResult == 0)
                        _api.WriteAcValueIndex(target, subgroup, setting, acValue);

                    (uint dcResult, uint dcValue) = _api.ReadDcValueIndex(source, subgroup, setting);
                    if (dcResult == 0)
                        _api.WriteDcValueIndex(target, subgroup, setting, dcValue);
                }
            }
        }

        /// <summary>改方案显示名。同名（忽略大小写）视为冲突。</summary>
        public (bool Success, string? Error) RenameScheme(Guid guid, string name)
        {
            if (guid == Guid.Empty)
                return (false, EmptySchemeIdMessage);

            (string? trimmed, string? nameError) = NormalizeSchemeName(name);
            if (trimmed is null)
                return (false, nameError);

            if (!SchemeExists(guid))
                return (false, SchemeNotFoundMessage);

            if (NameExists(trimmed, guid))
                return (false, SchemeNameExistsMessage);

            return _api.RenameScheme(guid, trimmed)
                ? (true, null)
                : (false, RenameSchemeFailedMessage);
        }

        /// <summary>
        /// 删除方案。内置的四个预设一律拒绝 —— 它们不是「用户建的东西」，
        /// 删掉之后系统里就再也派生不出对应模式的模板了。
        /// </summary>
        /// <remarks>
        /// 【活动方案要先换掉才能删】
        /// 系统直接拒绝删除当前生效的那一份，<c>PowerDeleteScheme</c> 返回非 0。
        /// 用户刚新建完一个方案想删掉时，它恰好就是活动的 —— 这条路径必然踩到。
        /// 这里先把活动切到任意一个别的方案，再删；切不走（只剩它一个）就如实报失败，
        /// 因为系统也不允许一个方案都不剩。
        /// </remarks>
        public (bool Success, string? Error) DeleteScheme(Guid guid)
        {
            if (guid == Guid.Empty)
                return (false, EmptySchemeIdMessage);

            if (PowerSchemePresets.IsBuiltIn(guid))
                return (false, BuiltInSchemeProtectedMessage);

            if (!SchemeExists(guid))
                return (false, SchemeNotFoundMessage);

            if (_api.GetActiveScheme() == guid && !SwitchAwayFrom(guid))
                return (false, DeleteSchemeFailedMessage);

            return _api.DeleteScheme(guid)
                ? (true, null)
                : (false, DeleteSchemeFailedMessage);
        }

        /// <summary>把活动方案换到任意一个不是 <paramref name="exclude"/> 的方案上。</summary>
        private bool SwitchAwayFrom(Guid exclude)
        {
            foreach ((Guid candidate, _) in _api.EnumerateSchemes())
            {
                if (candidate == exclude)
                    continue;

                if (_api.SetActiveScheme(candidate))
                    return true;
            }

            return false;
        }

        /// <summary>方案名归一化：去首尾空白后判空、判长。</summary>
        private static (string? Name, string? Error) NormalizeSchemeName(string? name)
        {
            string trimmed = name?.Trim() ?? string.Empty;

            if (trimmed.Length == 0)
                return (null, EmptySchemeNameMessage);

            if (trimmed.Length > MaxSchemeNameLength)
                return (null, SchemeNameTooLongMessage);

            return (trimmed, null);
        }

        /// <summary>本机是否存在这个 GUID 的方案。</summary>
        private bool SchemeExists(Guid guid)
        {
            foreach ((Guid existing, _) in _api.EnumerateSchemes())
            {
                if (existing == guid)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 除 <paramref name="except"/> 之外，是否已有同名方案。
        /// 忽略大小写：界面上两张卡片只有大小写不同，用户区分不出来。
        /// </summary>
        private bool NameExists(string name, Guid except)
        {
            foreach ((Guid guid, string? existing) in _api.EnumerateSchemes())
            {
                if (guid == except)
                    continue;

                if (string.Equals(existing?.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 写入的唯一出口。取活动方案 → 写 → 把 Win32 错误码原样带出去。
        /// </summary>
        private (bool Success, string? Error) WriteCore(
            Guid subgroup, Guid setting, uint value, string label, bool dc)
        {
            Guid? active = _api.GetActiveScheme();
            if (active is null)
                return (false, NoActiveSchemeMessage);

            uint result = dc
                ? _api.WriteDcValueIndex(active.Value, subgroup, setting, value)
                : _api.WriteAcValueIndex(active.Value, subgroup, setting, value);

            if (result == 0)
                return (true, null);

            return (false, $"{label}写入失败（错误 {result}）");
        }

        /// <summary>
        /// 读取失败的原样文案。
        ///
        /// 【为什么只留错误码，不带系统给出的说明文字】
        /// 这一串会作为逐项 Error 进入快照，再由 Worker 拼进 IPC 的 Message。
        /// 带上「参数错误。」这类本地化文本会让同一条故障在不同语言的系统上
        /// 长得不一样，测试也就无法用固定字符串断言。错误码本身是跨语言唯一的。
        /// </summary>
        private static string DescribeReadFailure(string label, uint code)
            => $"{label}读取失败（错误 {code}）";

        /// <summary>
        /// 范围校验失败的文案。
        ///
        /// 【为什么不能套用 Win32 格式】
        /// 这类失败根本没有发生过系统调用 —— 是我们在本地拦下来的。
        /// 写成「（错误 87）」会让排障的人去查 Win32 错误码表，
        /// 而真实原因只是「用户传了 101」。
        /// </summary>
        private static string DescribeOutOfRange(string label, int min, int max)
            => $"{label}超出允许范围（{min}-{max}）";
    }
}
