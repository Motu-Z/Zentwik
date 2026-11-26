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
}
