using TaoAdmin.Application.Services;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// 区划快照校验器缺口补测（存量补账班，分支 100% 硬门禁）：
/// 九条校验规则逐条打红——空包、非数字码、码长非法、重复码、空名称、
/// 层级码长不匹配、省级码尾、地级码尾、父子归属、省级数量、街道条数；合法快照放行。
/// </summary>
public class RegionSnapshotValidatorGapTests
{
    private static RegionNodeDto Node(string code, string name, int level, params RegionNodeDto[] children)
    {
        var n = new RegionNodeDto { Code = code, Name = name, Level = level };
        n.Children.AddRange(children);
        return n;
    }

    /// <summary>构造一份能通过全量校验的最小合法快照：多省 × 地级 × 区县 + 街道（9 位码，每区县至多 999 街道）。</summary>
    private static RegionSnapshot ValidSnapshot(int provinces = 2, int towns = 10000)
    {
        var snap = new RegionSnapshot { Version = "test-v1", Source = "unit-test" };
        var perProvince = towns / provinces;
        for (var p = 0; p < provinces; p++)
        {
            var provCode = $"{11 + p:00}0000";
            var cityCode = $"{11 + p:00}0100";
            var county = Node($"{11 + p:00}0101", "测试区", 3);
            var countySeq = 1;
            var remaining = perProvince;
            while (remaining > 0)
            {
                var take = Math.Min(999, remaining);
                var countyCode = $"{11 + p:00}01{countySeq:00}";
                var c = countySeq == 1 ? county : Node(countyCode, $"测试区{countySeq}", 3);
                for (var t = 0; t < take; t++)
                    c.Children.Add(Node($"{countyCode}{t:000}", $"街道{t}", 4));
                if (countySeq > 1) county.Children.Add(c);
                remaining -= take;
                countySeq++;
            }
            snap.Nodes.Add(Node(provCode, "测试省", 1, Node(cityCode, "测试市", 2, county)));
        }
        return snap;
    }

    [Fact]
    public void Validate_ValidSnapshot_Passes()
    {
        RegionSnapshotValidator.Validate(ValidSnapshot(provinces: 20, towns: 10000));
    }

    [Fact]
    public void Validate_EmptyNodes_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RegionSnapshotValidator.Validate(new RegionSnapshot()));
        Assert.Contains("数据包为空", ex.Message);
    }

    [Theory]
    [InlineData("11O000")]   // 含字母
    [InlineData("1100O1")]
    [InlineData("11001")]    // 5 位
    [InlineData("1100010")]  // 7 位
    public void Validate_NonDigitOrBadLength_Throws(string badCode)
    {
        var snap = new RegionSnapshot { Nodes = [Node(badCode, "省", 1)] };
        var ex = Assert.Throws<InvalidOperationException>(() => RegionSnapshotValidator.Validate(snap));
        Assert.Contains("6 位或 9 位数字", ex.Message);
    }

    [Fact]
    public void Validate_DuplicateCode_Throws()
    {
        var snap = new RegionSnapshot
        {
            Nodes = [Node("110000", "省一", 1), Node("110000", "省二", 1)]
        };
        var ex = Assert.Throws<InvalidOperationException>(() => RegionSnapshotValidator.Validate(snap));
        Assert.Contains("重复", ex.Message);
    }

    [Fact]
    public void Validate_EmptyName_Throws()
    {
        var snap = new RegionSnapshot { Nodes = [Node("110000", " ", 1)] };
        var ex = Assert.Throws<InvalidOperationException>(() => RegionSnapshotValidator.Validate(snap));
        Assert.Contains("名称为空", ex.Message);
    }

    [Fact]
    public void Validate_LevelCodeLengthMismatch_Throws()
    {
        // 层级 4 应为 9 位码
        var snap = new RegionSnapshot
        {
            Nodes = [Node("110000", "省", 1, Node("110101", "街道", 4))]
        };
        var ex = Assert.Throws<InvalidOperationException>(() => RegionSnapshotValidator.Validate(snap));
        Assert.Contains("长度应为 9 位", ex.Message);
    }

    [Fact]
    public void Validate_ProvinceCodeNotEnding0000_Throws()
    {
        var snap = new RegionSnapshot { Nodes = [Node("110100", "假省", 1)] };
        var ex = Assert.Throws<InvalidOperationException>(() => RegionSnapshotValidator.Validate(snap));
        Assert.Contains("以 0000 结尾", ex.Message);
    }

    [Fact]
    public void Validate_CityCodeEnding0000_Throws()
    {
        // 单独的地级节点（无父级）：码以 0000 结尾即触发地级码尾规则
        var snap = new RegionSnapshot { Nodes = [Node("110000", "假市", 2)] };
        var ex = Assert.Throws<InvalidOperationException>(() => RegionSnapshotValidator.Validate(snap));
        Assert.Contains("以 00 结尾且非省级码", ex.Message);
    }

    [Fact]
    public void Validate_ChildNotMatchingParentPrefix_Throws()
    {
        // 子级码前两位与父级不一致
        var snap = new RegionSnapshot
        {
            Nodes = [Node("110000", "北京", 1, Node("330100", "杭州", 2))]
        };
        var ex = Assert.Throws<InvalidOperationException>(() => RegionSnapshotValidator.Validate(snap));
        Assert.Contains("归属不一致", ex.Message);
    }

    [Fact]
    public void Validate_TooFewProvinces_Throws()
    {
        var snap = new RegionSnapshot { Nodes = [Node("110000", "独省", 1)] };
        var ex = Assert.Throws<InvalidOperationException>(() => RegionSnapshotValidator.Validate(snap));
        Assert.Contains("省级节点数量", ex.Message);
    }

    [Fact]
    public void Validate_TooFewTowns_Throws()
    {
        // 省级数量合法（20 个），但街道不足 10000
        var snap = new RegionSnapshot();
        for (var p = 0; p < 20; p++)
            snap.Nodes.Add(Node($"{11 + p:00}0000", $"省{p}", 1));
        var ex = Assert.Throws<InvalidOperationException>(() => RegionSnapshotValidator.Validate(snap));
        Assert.Contains("街道（乡镇）条数", ex.Message);
    }

    [Fact]
    public void Flatten_NestsChildrenWithParentCode()
    {
        var snap = ValidSnapshot(provinces: 1, towns: 0);
        var rows = snap.Flatten();
        Assert.Equal(3, rows.Count);
        Assert.Null(rows[0].ParentCode);
        Assert.Equal("110000", rows[1].ParentCode);
        Assert.Equal("110100", rows[2].ParentCode);
    }

    [Fact]
    public void CountByLevel_SumsEachLevel()
    {
        var snap = ValidSnapshot(provinces: 2, towns: 0);
        var (l1, l2, l3, l4) = RegionSnapshot.CountByLevel(snap.Flatten());
        Assert.Equal(2, l1);
        Assert.Equal(2, l2);
        Assert.Equal(2, l3);
        Assert.Equal(0, l4);
    }
}
