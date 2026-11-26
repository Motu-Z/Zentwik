using Xunit;
using Zentwik.Contracts.Protocol;
using Zentwik.Core.Plans;
using Zentwik.Core.Power;
using Zentwik.Domain.Power;
using Zentwik.Platform.Fakes;

namespace Zentwik.Core.Tests;

public sealed class PowerSchemeServiceTests
{
    private readonly FakePowerPolicyService _power = new();
    private readonly PowerSchemeService _service;

    public PowerSchemeServiceTests() => _service = new PowerSchemeService(_power);

    [Fact]
    public void List_Marks_Active_And_BuiltIn()
    {
        var schemes = _service.List();

        Assert.Contains(schemes, static scheme => scheme.Active && scheme.BuiltIn);
    }

    [Fact]
    public void Create_Duplicates_Template_And_Trims_Name()
    {
        var created = _service.Create(PowerSchemePresets.Balanced.Guid, "  我的方案  ");

        Assert.Equal("我的方案", created.Name);
        Assert.NotEqual(Guid.Empty, created.Guid);
        Assert.Contains(_service.List(), scheme => scheme.Guid == created.Guid && scheme.Name == "我的方案");
    }

    [Fact]
    public void Create_Rejects_Duplicate_Name()
    {
        _service.Create(PowerSchemePresets.Balanced.Guid, "重复名");

        var ex = Assert.Throws<CoreException>(() =>
            _service.Create(PowerSchemePresets.Balanced.Guid, "重复名"));

        Assert.Equal(ErrorCodes.PowerSchemeNameConflict, ex.Code);
    }

    [Fact]
    public void Create_Rejects_Empty_Name()
    {
        var ex = Assert.Throws<CoreException>(() =>
            _service.Create(PowerSchemePresets.Balanced.Guid, "   "));

        Assert.Equal(ErrorCodes.PayloadInvalid, ex.Code);
    }

    [Fact]
    public void Create_Rejects_Unknown_Template()
    {
        var ex = Assert.Throws<CoreException>(() =>
            _service.Create(Guid.NewGuid(), "新方案"));

        Assert.Equal(ErrorCodes.PowerSchemeNotFound, ex.Code);
    }

    [Fact]
    public void Delete_Rejects_BuiltIn()
    {
        var ex = Assert.Throws<CoreException>(() =>
            _service.Delete(PowerSchemePresets.Balanced.Guid));

        Assert.Equal(ErrorCodes.PowerSchemeBuiltIn, ex.Code);
    }

    [Fact]
    public void Delete_Active_Switches_Away_First()
    {
        var created = _service.Create(PowerSchemePresets.Balanced.Guid, "待删");
        _power.TrySetActiveScheme(created.Guid, out _);

        _service.Delete(created.Guid);

        Assert.DoesNotContain(_service.List(), scheme => scheme.Guid == created.Guid);
    }

    [Fact]
    public void Rename_Changes_Display_Name()
    {
        var created = _service.Create(PowerSchemePresets.Balanced.Guid, "旧名");

        var renamed = _service.Rename(created.Guid, "新名");

        Assert.Equal("新名", renamed.Name);
        Assert.Contains(_service.List(), scheme => scheme.Guid == created.Guid && scheme.Name == "新名");
    }

    [Fact]
    public void Rename_Rejects_Conflict()
    {
        _service.Create(PowerSchemePresets.Balanced.Guid, "甲");
        var second = _service.Create(PowerSchemePresets.Balanced.Guid, "乙");

        var ex = Assert.Throws<CoreException>(() => _service.Rename(second.Guid, "甲"));

        Assert.Equal(ErrorCodes.PowerSchemeNameConflict, ex.Code);
    }

    [Fact]
    public void Create_Keeps_Visible_Result_When_Template_Derives_Normally()
    {
        var created = _service.Create(PowerSchemePresets.HighPerformance.Guid, "正常派生");

        Assert.Contains(_service.List(), scheme => scheme.Guid == created.Guid);
    }

    [Fact]
    public void Create_Falls_Back_To_Balanced_When_Derived_Scheme_Is_Hidden()
    {
        // 模拟笔记本把「平衡」以外的方案标记为隐藏：派生品拿得到 GUID 却不出现在枚举里。
        _power.HiddenTemplates.Add(PowerSchemePresets.HighPerformance.Guid);

        var created = _service.Create(PowerSchemePresets.HighPerformance.Guid, "隐藏机器上的方案");

        Assert.Contains(_service.List(), scheme => scheme.Guid == created.Guid && scheme.Name == "隐藏机器上的方案");
    }

    [Fact]
    public void Create_Copies_Settings_From_Template_When_Falling_Back()
    {
        var subGroup = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        var setting = new Guid("33333333-3333-3333-3333-333333333333");
        _power.SetValue(PowerSchemePresets.HighPerformance.Guid, subGroup, setting, ac: true, 25);
        _power.SetValue(PowerSchemePresets.HighPerformance.Guid, subGroup, setting, ac: false, 75);
        _power.HiddenTemplates.Add(PowerSchemePresets.HighPerformance.Guid);

        var created = _service.Create(PowerSchemePresets.HighPerformance.Guid, "搬设置");

        Assert.True(_power.TryReadValue(created.Guid, subGroup, setting, ac: true, out var ac, out _));
        Assert.Equal(25u, ac);
        Assert.True(_power.TryReadValue(created.Guid, subGroup, setting, ac: false, out var dc, out _));
        Assert.Equal(75u, dc);
    }

    [Fact]
    public void Create_Copies_Description_From_Template_When_Falling_Back()
    {
        _power.TryWriteDescription(PowerSchemePresets.HighPerformance.Guid, "偏向性能", out _);
        _power.HiddenTemplates.Add(PowerSchemePresets.HighPerformance.Guid);

        var created = _service.Create(PowerSchemePresets.HighPerformance.Guid, "搬说明");

        Assert.Equal("偏向性能", _power.ReadDescription(created.Guid));
    }

    [Fact]
    public void Create_Keeps_Ultimate_Layered_On_Top_Of_High_Performance()
    {
        var subGroup = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        var highOnly = new Guid("11111111-1111-1111-1111-111111111111");
        var ultimateOwn = new Guid("22222222-2222-2222-2222-222222222222");

        _power.SetValue(PowerSchemePresets.HighPerformance.Guid, subGroup, highOnly, ac: true, 40);
        _power.SetValue(PowerSchemePresets.UltimatePerformance.Guid, subGroup, ultimateOwn, ac: true, 90);
        _power.HiddenTemplates.Add(PowerSchemePresets.UltimatePerformance.Guid);

        var created = _service.Create(PowerSchemePresets.UltimatePerformance.Guid, "卓越");

        Assert.True(_power.TryReadValue(created.Guid, subGroup, highOnly, ac: true, out var inherited, out _));
        Assert.Equal(40u, inherited);
        Assert.True(_power.TryReadValue(created.Guid, subGroup, ultimateOwn, ac: true, out var own, out _));
        Assert.Equal(90u, own);
    }

    // 隐藏方案（键存在、可读写，但不在枚举里）必须和可见方案一样能被定位、改名、删除、查重。
    // 早前实现用枚举做存在性判断，对这类方案会报「不存在」——那是把可见性当成了存在性。

    [Fact]
    public void List_Shows_Hidden_Schemes_With_Visible_False()
    {
        var hidden = CreateHiddenScheme("隐藏的");

        var entry = Assert.Single(_service.List(), scheme => scheme.Guid == hidden);

        Assert.False(entry.Visible);
        Assert.Contains(_service.List(), static scheme => scheme.Visible);
    }

    [Fact]
    public void Rename_Works_On_Hidden_Scheme()
    {
        var hidden = CreateHiddenScheme("隐藏原名");

        var renamed = _service.Rename(hidden, "隐藏新名");

        Assert.Equal("隐藏新名", renamed.Name);
        Assert.False(renamed.Visible);
        Assert.Contains(_service.List(), scheme => scheme.Guid == hidden && scheme.Name == "隐藏新名");
    }

    [Fact]
    public void Delete_Removes_Hidden_Scheme()
    {
        var hidden = CreateHiddenScheme("待删的隐藏方案");

        _service.Delete(hidden);

        Assert.DoesNotContain(_service.List(), scheme => scheme.Guid == hidden);
    }

    [Fact]
    public void Create_Rejects_Name_Taken_By_Hidden_Scheme()
    {
        CreateHiddenScheme("隐藏占用");

        var ex = Assert.Throws<CoreException>(() =>
            _service.Create(PowerSchemePresets.Balanced.Guid, "隐藏占用"));

        Assert.Equal(ErrorCodes.PowerSchemeNameConflict, ex.Code);
    }

    [Fact]
    public void Delete_Active_Switches_To_Visible_Scheme()
    {
        // 隐藏方案不能当切换目标：切过去会让它临时冒进枚举，用户会以为自己多了个方案。
        _ = CreateHiddenScheme("隐藏目标");
        var victim = _service.Create(PowerSchemePresets.Balanced.Guid, "待删活动");
        _power.TrySetActiveScheme(victim.Guid, out _);

        _service.Delete(victim.Guid);

        var active = _power.GetActiveScheme();
        Assert.NotNull(active);
        Assert.NotEqual(victim.Guid, active.Guid);
        Assert.Contains(_service.List(), scheme => scheme.Guid == active.Guid && scheme.Visible);
    }

    private Guid CreateHiddenScheme(string name)
    {
        _power.HiddenTemplates.Add(PowerSchemePresets.HighPerformance.Guid);
        var guid = _power.DuplicateScheme(PowerSchemePresets.HighPerformance.Guid, out _)!.Value;
        _power.RenameScheme(guid, name, out _);
        return guid;
    }
}
