using System.Text;
using SqlSugar;
using TaoAdmin.Application.Services;
using TaoAdmin.Common.Snowflake;
using TaoAdmin.Domain;
using TaoAdmin.Domain.Entities;
using Xunit;

namespace TaoAdmin.Tests.Application;

/// <summary>
/// 用户表格导入导出应用服务（SQLite 内存库真库测试）：
/// 模板生成、导出不含密码、导入逐行校验与错误明细。
/// </summary>
public class UserExcelAppServiceTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly UserExcelAppService _svc;
    private readonly MemoryStream _stream;

    public UserExcelAppServiceTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "DataSource=:memory:",
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false
        });
        _db.CodeFirst.InitTables<SysUser>();
        _svc = new UserExcelAppService(_db, new SnowflakeIdGenerator(1, 1), new DefaultPolicyStub());
        _stream = new MemoryStream();
    }

    public void Dispose() => _stream.Dispose();

    [Fact]
    public async Task BuildTemplateAsync_ShouldProduceReadableXlsx_WithHeaderAndSampleRow()
    {
        var bytes = await _svc.BuildTemplateAsync();
        Assert.True(bytes.Length > 0);
        // 模板可被自身导入流程解析（表头 + 示例行）：示例行用户名 zhangsan 校验通过后为 1 行有效
        var ms = new MemoryStream(bytes);
        var result = await _svc.ImportAsync(ms, 1);
        Assert.Equal(1, result.total);
        Assert.Equal(1, result.success);
        Assert.Empty(result.errors);
        // 导入未指定密码：落默认密码哈希（非明文）
        var user = Assert.Single(_db.Queryable<SysUser>().ToList());
        Assert.Equal("zhangsan", user.UserName);
        Assert.NotEqual(UserExcelAppService.DefaultImportPassword, user.Password);
        Assert.True(BCrypt.Net.BCrypt.Verify(UserExcelAppService.DefaultImportPassword, user.Password));
    }

    [Fact]
    public async Task ExportAsync_ShouldContainUserName_ButNotPassword()
    {
        _db.Insertable(new SysUser
        {
            Id = 1, UserName = "admin", Password = BCrypt.Net.BCrypt.HashPassword("Admin@123"),
            NickName = "管理员", Status = 1
        }).ExecuteCommand();

        var bytes = await _svc.ExportAsync();
        // xlsx 是 zip 压缩流，不能在原始字节上做子串断言；用 MiniExcel 读回逐行验内容（首行作表头）
        var back = MiniExcelLibs.MiniExcel.Query(new MemoryStream(bytes), true)
            .Cast<IDictionary<string, object?>>().ToList();
        string Cell(IDictionary<string, object?> r, string col)
            => r.TryGetValue(col, out var v) && v != null ? v.ToString()!.Trim() : "";
        var adminRow = Assert.Single(back, r => Cell(r, "用户名") == "admin");
        Assert.Equal("管理员", Cell(adminRow, "昵称"));
        // 密码列恒为空：真实口令与哈希都不导出
        Assert.Equal("", Cell(adminRow, UserExcelAppService.Columns[5]));
    }

    [Fact]
    public async Task ImportAsync_DuplicateUsernameInFile_ShouldReportErrorRows()
    {
        var bytes = await _svc.BuildTemplateAsync();
        // 构造含重复用户名的表格：直接用 MiniExcel 写两行同用户名
        var rows = new List<Dictionary<string, object>>
        {
            new() { ["用户名"] = "lisi", ["昵称"] = "李四", ["邮箱"] = "lisi@example.com", ["手机号"] = "", ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "" },
            new() { ["用户名"] = "lisi", ["昵称"] = "李四二", ["邮箱"] = "", ["手机号"] = "", ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "" },
        };
        var outMs = new MemoryStream();
        await MiniExcelLibs.MiniExcel.SaveAsAsync(outMs, rows, sheetName: "用户导入模板");
        var result = await _svc.ImportAsync(new MemoryStream(outMs.ToArray()), 1);

        Assert.Equal(2, result.total);
        Assert.Equal(1, result.success);   // 首行成功
        var err = Assert.Single(result.errors);
        Assert.Contains("重复", err.reason);
    }

    [Fact]
    public async Task ImportAsync_ExistingUsernameInDb_ShouldNotOverwrite_AndReportError()
    {
        _db.Insertable(new SysUser
        {
            Id = 7, UserName = "wangwu", Password = BCrypt.Net.BCrypt.HashPassword("Aa@123456"),
            NickName = "老王", Status = 1
        }).ExecuteCommand();

        var outMs = new MemoryStream();
        await MiniExcelLibs.MiniExcel.SaveAsAsync(outMs, new List<Dictionary<string, object>>
        {
            new() { ["用户名"] = "wangwu", ["昵称"] = "新王", ["邮箱"] = "", ["手机号"] = "", ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "" },
        }, sheetName: "用户导入模板");
        var result = await _svc.ImportAsync(new MemoryStream(outMs.ToArray()), 1);

        Assert.Equal(0, result.success);
        var err = Assert.Single(result.errors);
        Assert.Contains("已存在", err.reason);
        // 原用户未被覆盖
        var user = _db.Queryable<SysUser>().First(u => u.UserName == "wangwu");
        Assert.Equal("老王", user.NickName);
    }

    [Fact]
    public async Task ImportAsync_ShortPassword_AndBadStatus_ShouldReportReasons()
    {
        var outMs = new MemoryStream();
        await MiniExcelLibs.MiniExcel.SaveAsAsync(outMs, new List<Dictionary<string, object>>
        {
            new() { ["用户名"] = "aa", ["昵称"] = "", ["邮箱"] = "", ["手机号"] = "", ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "123" },
            new() { ["用户名"] = "bb", ["昵称"] = "", ["邮箱"] = "", ["手机号"] = "", ["状态(启用/禁用)"] = "活着", ["密码(仅导入,留空用默认密码)"] = "" },
        }, sheetName: "用户导入模板");
        var result = await _svc.ImportAsync(new MemoryStream(outMs.ToArray()), 1);

        Assert.Equal(0, result.success);
        Assert.Equal(2, result.errors.Count);
        Assert.Contains(result.errors, e => e.reason.Contains("密码"));
        Assert.Contains(result.errors, e => e.reason.Contains("状态"));
        Assert.Equal(0, _db.Queryable<SysUser>().Count());
    }

    [Fact]
    public async Task ImportAsync_BlankUserName_WithOtherCellFilled_ShouldReportError()
    {
        // 用户名为空但昵称有值：不算整行空白，必须报「用户名不能为空」
        var outMs = new MemoryStream();
        await MiniExcelLibs.MiniExcel.SaveAsAsync(outMs, new List<Dictionary<string, object>>
        {
            new() { ["用户名"] = "", ["昵称"] = "无名氏", ["邮箱"] = "", ["手机号"] = "", ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "" },
        }, sheetName: "用户导入模板");
        var result = await _svc.ImportAsync(new MemoryStream(outMs.ToArray()), 1);

        Assert.Equal(1, result.total);
        Assert.Equal(0, result.success);
        var err = Assert.Single(result.errors);
        Assert.Equal(1, err.row);
        Assert.Contains("用户名不能为空", err.reason);
        Assert.Equal(0, _db.Queryable<SysUser>().Count());
    }

    [Fact]
    public async Task ImportAsync_OverLongUserName_ShouldReportError()
    {
        // 用户名 51 个字符：超过 50 上限被拦
        var outMs = new MemoryStream();
        await MiniExcelLibs.MiniExcel.SaveAsAsync(outMs, new List<Dictionary<string, object>>
        {
            new() { ["用户名"] = new string('u', 51), ["昵称"] = "", ["邮箱"] = "", ["手机号"] = "", ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "" },
        }, sheetName: "用户导入模板");
        var result = await _svc.ImportAsync(new MemoryStream(outMs.ToArray()), 1);

        Assert.Equal(0, result.success);
        var err = Assert.Single(result.errors);
        Assert.Contains("超过 50 个字符", err.reason);
        Assert.Equal(0, _db.Queryable<SysUser>().Count());
    }

    [Fact]
    public async Task ImportAsync_OverLongEmail_AndPhone_ShouldReportErrors()
    {
        // 邮箱 101 字符、手机号 21 字符：分别报「邮箱过长」「手机号过长」
        var outMs = new MemoryStream();
        await MiniExcelLibs.MiniExcel.SaveAsAsync(outMs, new List<Dictionary<string, object>>
        {
            new() { ["用户名"] = "mailman", ["昵称"] = "", ["邮箱"] = new string('e', 101), ["手机号"] = "", ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "" },
            new() { ["用户名"] = "phoneman", ["昵称"] = "", ["邮箱"] = "", ["手机号"] = new string('1', 21), ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "" },
        }, sheetName: "用户导入模板");
        var result = await _svc.ImportAsync(new MemoryStream(outMs.ToArray()), 1);

        Assert.Equal(2, result.total);
        Assert.Equal(0, result.success);
        Assert.Equal(2, result.errors.Count);
        Assert.Contains(result.errors, e => e.reason.Contains("邮箱过长"));
        Assert.Contains(result.errors, e => e.reason.Contains("手机号过长"));
        Assert.Equal(0, _db.Queryable<SysUser>().Count());
    }

    [Fact]
    public async Task ImportAsync_GarbageStream_ShouldThrowDomainException()
    {
        // 非 xlsx 字节流：解析失败统一抛 DomainException，提示用模板
        var ms = new MemoryStream(Encoding.UTF8.GetBytes("this is not an excel file at all"));
        var ex = await Assert.ThrowsAsync<DomainException>(() => _svc.ImportAsync(ms, 1));
        Assert.Contains("表格解析失败", ex.Message);
        Assert.Equal(0, _db.Queryable<SysUser>().Count());
    }

    [Fact]
    public async Task ImportAsync_ValidRow_WithPhoneDisabledAndCustomPassword_ShouldPersistAll()
    {
        // 手机号有值 + 状态禁用 + 自定义密码：三个未覆盖落库分支一次打满
        var outMs = new MemoryStream();
        await MiniExcelLibs.MiniExcel.SaveAsAsync(outMs, new List<Dictionary<string, object>>
        {
            new() { ["用户名"] = "zhaoliu", ["昵称"] = "赵六", ["邮箱"] = "zhaoliu@example.com", ["手机号"] = "13900139000", ["状态(启用/禁用)"] = "禁用", ["密码(仅导入,留空用默认密码)"] = "Xy@98765" },
        }, sheetName: "用户导入模板");
        var result = await _svc.ImportAsync(new MemoryStream(outMs.ToArray()), 1);

        Assert.Equal(1, result.success);
        Assert.Empty(result.errors);
        var user = Assert.Single(_db.Queryable<SysUser>().ToList());
        Assert.Equal("zhaoliu", user.UserName);
        Assert.Equal(0, user.Status);          // 禁用 → 0
        Assert.Equal("13900139000", user.Phone); // 手机号有值落库
        Assert.True(BCrypt.Net.BCrypt.Verify("Xy@98765", user.Password)); // 自定义密码哈希
    }

    [Fact]
    public async Task ImportAsync_EmptyNickName_ShouldFallBackToUserName()
    {
        // 昵称留空：落库昵称回退为用户名
        var outMs = new MemoryStream();
        await MiniExcelLibs.MiniExcel.SaveAsAsync(outMs, new List<Dictionary<string, object>>
        {
            new() { ["用户名"] = "sunqi", ["昵称"] = "", ["邮箱"] = "", ["手机号"] = "", ["状态(启用/禁用)"] = "启用", ["密码(仅导入,留空用默认密码)"] = "" },
        }, sheetName: "用户导入模板");
        var result = await _svc.ImportAsync(new MemoryStream(outMs.ToArray()), 1);

        Assert.Equal(1, result.success);
        var user = Assert.Single(_db.Queryable<SysUser>().ToList());
        Assert.Equal("sunqi", user.NickName); // 昵称回退用户名
        Assert.Null(user.Email);
        Assert.Null(user.Phone);
        Assert.Equal(1, user.Status);
    }

    [Fact]
    public async Task ExportAsync_DisabledUser_WithNullFields_ShouldRenderDisabledAndEmpty()
    {
        // 禁用用户 + 昵称/邮箱为 null：导出状态列转「禁用」，空字段转空串
        _db.Insertable(new SysUser
        {
            Id = 9, UserName = "qianqi", Password = BCrypt.Net.BCrypt.HashPassword("Aa@123456"),
            NickName = null, Email = null, Phone = null, Status = 0
        }).ExecuteCommand();

        var bytes = await _svc.ExportAsync();
        var back = MiniExcelLibs.MiniExcel.Query(new MemoryStream(bytes), true)
            .Cast<IDictionary<string, object?>>().ToList();
        string Cell(IDictionary<string, object?> r, string col)
            => r.TryGetValue(col, out var v) && v != null ? v.ToString()!.Trim() : "";
        var row = Assert.Single(back, r => Cell(r, "用户名") == "qianqi");
        Assert.Equal("禁用", Cell(row, "状态(启用/禁用)"));
        Assert.Equal("", Cell(row, "昵称"));
        Assert.Equal("", Cell(row, "邮箱"));
    }
}
