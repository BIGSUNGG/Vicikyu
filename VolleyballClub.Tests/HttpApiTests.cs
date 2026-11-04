using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Domain.Enums;
using VolleyballClub.Infrastructure.Identity;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Tests;

public sealed class HttpApiTests
{
    [Fact]
    public async Task Authentication_Csrf_Ownership_And_Logout()
    {
        await using var host = await ApiHost.StartAsync();
        using var client = host.Client();
        await Status(client.GetAsync("/api/my/profile"), HttpStatusCode.Unauthorized);
        await Status(client.PostAsJsonAsync("/api/auth/login", new LoginRequest("member", "pass")), HttpStatusCode.BadRequest);
        await Login(client, "member");
        await Status(client.GetAsync("/api/admin/members/member-id"), HttpStatusCode.Forbidden);
        var profile = await client.GetFromJsonAsync<ProfileDto>("/api/my/profile?userId=admin-id");
        Assert.Equal("member", profile!.StudentNumber);
        Assert.Equal("no-store", (await client.GetAsync("/api/my/profile")).Headers.CacheControl!.ToString());
        // The pre-login token is bound to the anonymous identity; it must not authorize authenticated writes.
        await Status(client.PutAsJsonAsync("/api/my/profile/color", new TextRequest("invalid")), HttpStatusCode.BadRequest);
        await Token(client);
        await Status(client.PutAsJsonAsync("/api/my/profile/color", new TextRequest("invalid")), HttpStatusCode.BadRequest);
        await Status(client.PostAsync("/api/auth/logout", null), HttpStatusCode.NoContent);
        await Status(client.GetAsync("/api/my/profile"), HttpStatusCode.Unauthorized);
        await Status(client.PostAsJsonAsync("/api/auth/login", new LoginRequest("member", "pass")), HttpStatusCode.BadRequest);
        await Login(client, "member");
        await Token(client);
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Status(client.PostAsync("/api/auth/logout", null), HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("role")]
    [InlineData("stamp")]
    [InlineData("password")]
    public async Task Cookie_Is_Revoked_On_Every_Request(string change)
    {
        await using var host = await ApiHost.StartAsync();
        using var client = host.Client();
        await Login(client, "admin");
        using (var scope = host.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync("admin-id"))!;
            IdentityResult result;
            if (change == "status") { user.Status = MemberStatus.Withdrawn; result = await users.UpdateAsync(user); }
            else if (change == "role") result = await users.RemoveFromRoleAsync(user, "ADMIN");
            else if (change == "password") result = await users.ChangePasswordAsync(user, "pass", "newpass");
            else result = await users.UpdateSecurityStampAsync(user);
            Assert.True(result.Succeeded);
        }
        await Status(client.GetAsync("/api/auth/user"), HttpStatusCode.Unauthorized);
        await Status(client.GetAsync("/api/admin/teams"), HttpStatusCode.Unauthorized);
        if (change == "status")
        {
            await Token(client);
            await Status(client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "pass")), HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task Business_Apis_Keep_Dtos_Validation_Error_Statuses_And_Audit()
    {
        await using var host = await ApiHost.StartAsync();
        using var admin = host.Client();
        using var member = host.Client();
        await Login(admin, "admin"); await Token(admin);
        await Login(member, "member"); await Token(member);
        var now = VolleyballClub.Domain.KstClock.Today(TimeProvider.System);
        await Status(member.GetAsync("/api/my/fees/current"), HttpStatusCode.NoContent);
        await Status(member.GetAsync("/api/my/calendar?year=2026&month=13"), HttpStatusCode.BadRequest);
        await Status(admin.GetAsync("/api/admin/members/missing"), HttpStatusCode.NotFound);
        await Status(admin.PostAsJsonAsync("/api/admin/members", new MemberCreateRequest("member", "duplicate", "", "pass")), HttpStatusCode.Conflict);
        await Status(admin.PostAsJsonAsync("/api/admin/members", new { studentNumber = "new", name = (string?)null, department = "", password = "pass" }), HttpStatusCode.BadRequest);
        await Status(admin.PutAsJsonAsync("/api/admin/members/member-id", new MemberUpdateRequest("member", "", Role: "ROOT")), HttpStatusCode.BadRequest);
        var today = await Read<AdminTodayDto>(admin.GetAsync("/api/admin/activity/today"));
        Assert.Null(today.Status); // 시작 전 — 행 없는 가상 '준비 중'
        await Status(admin.PostAsync("/api/admin/activity/new", null), HttpStatusCode.OK);
        var otp = await Read<string>(admin.PostAsync("/api/admin/activity/otp", null));
        await Status(member.PostAsJsonAsync("/api/my/attendance", new TextRequest("wrong")), HttpStatusCode.BadRequest);
        await Status(member.PostAsJsonAsync("/api/my/attendance", new TextRequest(otp)), HttpStatusCode.OK);
        Assert.True((await member.GetFromJsonAsync<MyTodayDto>("/api/my/activity"))!.IsAttended);
        await Status(admin.PostAsync("/api/admin/attendance/admin-id", null), HttpStatusCode.NoContent);
        var teams = await Read<List<TeamResultDto>>(admin.PostAsJsonAsync("/api/admin/teams/generate", new TeamCreateRequest(["member-id", "admin-id"], [], [1, 1])));
        Assert.Equal(2, teams.Count);
        await Status(admin.PostAsync("/api/admin/teams/confirm", null), HttpStatusCode.NoContent);
        await Status(admin.DeleteAsync("/api/admin/attendance/member-id"), HttpStatusCode.BadRequest);
        await Status(SendDelete(admin, "/api/admin/attendance/member-id", new AttendanceChangeRequest()), HttpStatusCode.Conflict);
        await Status(SendDelete(admin, "/api/admin/attendance/member-id", new AttendanceChangeRequest(true)), HttpStatusCode.NoContent);
        var period = await Read<FeePeriodDto>(admin.PostAsJsonAsync("/api/admin/fees", new FeePeriodCreateRequest("HTTP test", now, now.AddDays(1), 100)));
        await Status(admin.PutAsJsonAsync($"/api/admin/fees/{period.Id}/members/member-id", new FeePaymentRequest(true)), HttpStatusCode.NoContent);
        Assert.True((await member.GetFromJsonAsync<List<FeeStatusDto>>("/api/my/fees"))!.Single().IsPaid);
        await Status(admin.PostAsJsonAsync("/api/admin/semesters", new SemesterSaveRequest("HTTP semester", now, now.AddDays(30))), HttpStatusCode.OK);
        // 활동 기록 xlsx 가져오기·내보내기 — 신규 관리자 설정 계약
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(TestWorkbook.Write(("2026-2",
            [
                ["활동 상태", "활동 완료"],
                ["총 인원 : ", "26. 09. 01. 화"],
                ["member", "출석"],
            ]))), "file", "activities.xlsx");
            var report = await Read<ActivityImportReport>(admin.PostAsync("/api/admin/activities/import", form));
            Assert.Equal(1, report.ImportedActivities);
            Assert.Equal(1, report.ImportedAttendances);
        }
        var exported = await admin.GetByteArrayAsync("/api/admin/activities/export");
        Assert.True(exported.Length > 4);
        Assert.Equal(0x50, exported[0]); // xlsx(zip) 매직 바이트 'P'
        Assert.Empty((await admin.GetFromJsonAsync<List<SemesterMemberRowDto>>("/api/admin/semester-members?mapped=false"))!);
        var audit = await admin.GetFromJsonAsync<List<AuditLogDto>>("/api/admin/audit?take=100");
        Assert.Contains(audit!, a => a.Action == AdminAction.SettingChange); // 가져오기 감사
        await Status(member.PutAsJsonAsync("/api/my/profile/password", new PasswordChangeRequest("pass", "newpass")), HttpStatusCode.NoContent);
        await Status(member.GetAsync("/api/my/profile"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Activity_Import_Does_Not_Persist_The_Upload_File_On_Disk()
    {
        await using var host = await ApiHost.StartAsync();
        using var admin = host.Client();
        await Login(admin, "admin");
        await Token(admin);

        static List<string> UploadCandidates() =>
        [
            .. Directory.EnumerateFiles(Path.GetTempPath())
                .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory))
                .Where(f => Path.GetFileName(f).EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(f).EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => $"{f}:{new FileInfo(f).Length}"),
        ];

        var before = UploadCandidates();
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(TestWorkbook.Write(("2026-2",
            [
                ["활동 상태", "활동 완료"],
                ["총 인원 : ", "26. 09. 01. 화"],
                ["미매칭마커", "출석"],
            ]))), "file", "no-persist-probe.xlsx");
            var report = await Read<ActivityImportReport>(admin.PostAsync("/api/admin/activities/import", form));
            Assert.Equal(1, report.ImportedActivities);
            Assert.Equal(1, report.UnmappedMembers);
        }
        // 업로드 파일은 스트리밍으로 파싱·폐기된다 — 임시·콘텐츠 루트에 새 파일이 생기면 안 된다
        Assert.Equal(before, UploadCandidates());
    }

    [Fact]
    public async Task Shell_Routes_Are_Public_Wasm_Not_Server_Circuits()
    {
        await using var host = await ApiHost.StartAsync();
        using var client = host.Client();
        foreach (var route in new[] { "/", "/Account/Login", "/Account/Logout", "/admin", "/admin/members/member-id", "/my", "/records" })
        {
            var response = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("_framework/blazor.web.js", html);
            Assert.Contains("webassembly", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"type\":\"server\"", html);
        }
        await Status(client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Own_Password_Change_Refreshes_Browser_Identity_And_Csrf()
    {
        using var handler = new PasswordChangeHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost") };
        var api = new VolleyballClub.Client.Services.ApiHttp(http);
        var auth = new VolleyballClub.Client.Services.CookieAuthenticationStateProvider(http, api);
        var members = new VolleyballClub.Client.Services.MemberClient(api);
        Assert.True((await auth.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        await members.ChangeMyPasswordAsync(new PasswordChangeRequest("pass", "newpass"));
        Assert.False((await auth.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated);
        Assert.Equal(2, handler.TokenRequests);
    }

    [Fact]
    public async Task Bulk_Http_Requires_Admin_Csrf_And_Preview_Is_Read_Only()
    {
        await using var host = await ApiHost.StartAsync();
        using var admin = host.Client();
        using var member = host.Client();
        using var anonymous = host.Client();
        var bulk = new MemberBulkRequest([new("http-new", "합성 부원", "", "test-only")]);
        await Token(anonymous);
        await Status(anonymous.PostAsJsonAsync("/api/admin/members/bulk", bulk), HttpStatusCode.Unauthorized);
        await Login(member, "member"); await Token(member);
        await Status(member.PostAsJsonAsync("/api/admin/members/bulk", bulk), HttpStatusCode.Forbidden);
        await Login(admin, "admin"); await Token(admin);
        var before = (await Read<MemberPageDto>(admin.PostAsJsonAsync("/api/admin/members/search", new MemberPageRequest()))).TotalCount;
        MultipartFormDataContent Upload(string role = "MEMBER", string password = "test-only", string name = "synthetic.xlsx")
        {
            var form = new MultipartFormDataContent();
            form.Add(new StreamContent(MemberWorkbookTests.Workbook(MemberWorkbookTests.ValidRow)), "file", name);
            form.Add(new StringContent(role), "role");
            form.Add(new StringContent(password), "password");
            return form;
        }
        using (var form = Upload()) await Status(member.PostAsync("/api/admin/members/import", form), HttpStatusCode.Forbidden);
        using (var form = Upload("ROOT")) await Status(admin.PostAsync("/api/admin/members/import", form), HttpStatusCode.BadRequest);
        using (var form = Upload(password: "123")) await Status(admin.PostAsync("/api/admin/members/import", form), HttpStatusCode.BadRequest);
        using (var form = Upload(password: "    ")) await Status(admin.PostAsync("/api/admin/members/import", form), HttpStatusCode.BadRequest);
        var blankPassword = await Read<MemberBulkResult>(admin.PostAsJsonAsync("/api/admin/members/bulk", new MemberBulkRequest([bulk.Rows[0] with { Password = "    " }])));
        Assert.Empty(blankPassword.Created); Assert.Contains(blankPassword.Errors, e => e.Row == 1);
        using (var form = Upload(name: "synthetic.xls")) await Status(admin.PostAsync("/api/admin/members/import", form), HttpStatusCode.BadRequest);
        using (var form = Upload())
        {
            var rows = await Read<List<MemberCreateRequest>>(admin.PostAsync("/api/admin/members/import", form));
            Assert.Single(rows); Assert.Equal("209912345", rows[0].StudentNumber);
        }
        Assert.Equal(before, (await Read<MemberPageDto>(admin.PostAsJsonAsync("/api/admin/members/search", new MemberPageRequest()))).TotalCount);
        var invalid = await Read<MemberBulkResult>(admin.PostAsJsonAsync("/api/admin/members/bulk", new MemberBulkRequest([bulk.Rows[0], bulk.Rows[0] with { StudentNumber = "admin" }])));
        Assert.Empty(invalid.Created); Assert.Contains(invalid.Errors, e => e.Row == 2);
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using (var form = Upload()) await Status(admin.PostAsync("/api/admin/members/import", form), HttpStatusCode.BadRequest);
        await Status(admin.PostAsJsonAsync("/api/admin/members/bulk", bulk), HttpStatusCode.BadRequest);
        await Token(admin);
        using (var failureScope = host.Services.CreateScope())
        {
            var failureDb = failureScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await failureDb.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_http_bulk BEFORE INSERT ON AuditLogs WHEN (SELECT COUNT(*) FROM AuditLogs) = 1 BEGIN SELECT RAISE(ABORT, 'synthetic audit failure'); END;");
            var failed = await Read<MemberBulkResult>(admin.PostAsJsonAsync("/api/admin/members/bulk", new MemberBulkRequest([bulk.Rows[0], bulk.Rows[0] with { StudentNumber = "http-second" }])));
            Assert.Empty(failed.Created); Assert.Contains(failed.Errors, e => e.Row == 2);
            Assert.Equal(before, await failureDb.Users.CountAsync());
            Assert.Equal(before, await failureDb.UserRoles.CountAsync());
            Assert.Equal(0, await failureDb.AuditLogs.CountAsync());
            await failureDb.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_http_bulk;");
        }
        var result = await Read<MemberBulkResult>(admin.PostAsJsonAsync("/api/admin/members/bulk", bulk));
        Assert.Empty(result.Errors); Assert.Single(result.Created);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(before + 1, await db.Users.CountAsync());
        Assert.Equal(1, await db.UserRoles.CountAsync(r => r.UserId == result.Created[0].UserId));
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.TargetId == result.Created[0].UserId));
        using var newMember = host.Client();
        await Token(newMember);
        await Status(newMember.PostAsJsonAsync("/api/auth/login", new LoginRequest("http-new", "test-only")), HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Concurrent_Bulk_Duplicate_Race_Commits_Only_One_Whole_List()
    {
        await using var host = await ApiHost.StartAsync();
        using var first = host.Client(); using var second = host.Client();
        await Login(first, "admin"); await Token(first);
        await Login(second, "admin"); await Token(second);
        MemberBulkRequest Request(string unique) => new([new(unique, "합성 부원", "", "test-only"), new("shared", "합성 부원", "", "test-only")]);
        var results = await Task.WhenAll(
            Read<MemberBulkResult>(first.PostAsJsonAsync("/api/admin/members/bulk", Request("first-only"))),
            Read<MemberBulkResult>(second.PostAsJsonAsync("/api/admin/members/bulk", Request("second-only"))));
        Assert.Single(results, r => r.Created.Count == 2 && r.Errors.Count == 0);
        Assert.Single(results, r => r.Created.Count == 0 && r.Errors.Count > 0);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(4, await db.Users.CountAsync());
        Assert.Equal(4, await db.UserRoles.CountAsync());
        Assert.Equal(2, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Managed_activity_http_contract_requires_admin_csrf_and_rejects_invalid_kinds()
    {
        await using var host = await ApiHost.StartAsync();
        using var anonymous = host.Client(); using var member = host.Client(); using var admin = host.Client();
        await Token(anonymous); await Login(member, "member"); await Token(member);
        foreach (var path in new[] { "/api/admin/activities", "/api/admin/activities/key/members", "/api/admin/members/member-id/activities" })
        {
            await Status(anonymous.GetAsync(path), HttpStatusCode.Unauthorized);
            await Status(member.GetAsync(path), HttpStatusCode.Forbidden);
        }
        await Login(admin, "admin"); await Token(admin);
        var request = new ActivityCreateRequest(new(2026, 10, 2), DayKind.NoActivity);
        await Status(anonymous.PostAsJsonAsync("/api/admin/activities", request), HttpStatusCode.Unauthorized);
        await Status(member.PostAsJsonAsync("/api/admin/activities", request), HttpStatusCode.Forbidden);
        admin.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        await Status(admin.PostAsJsonAsync("/api/admin/activities", request), HttpStatusCode.BadRequest);
        await Token(admin);
        await Status(admin.PostAsJsonAsync("/api/admin/activities", request with { Kind = (DayKind)999 }), HttpStatusCode.BadRequest);
        var row = await Read<ManagedActivityDto>(admin.PostAsJsonAsync("/api/admin/activities", request));
        Assert.Null(row.Note); Assert.False(row.CanEditAttendance);
        // 예정/없음 표시에는 출석이 없다 — 활동 키가 아니므로 찾을 수 없음
        await Status(admin.PutAsJsonAsync($"/api/admin/activities/{row.Key}/members/member-id", new ActivityAttendanceRequest(true)), HttpStatusCode.NotFound);
        var completed = await Read<ManagedActivityDto>(admin.PutAsJsonAsync($"/api/admin/activities/{row.Key}", new ActivityEditRequest(request.Date, DayKind.Completed)));
        Assert.NotNull(completed.ActivityId); Assert.True(completed.CanEditAttendance);
        await Status(admin.PostAsync($"/api/admin/members/member-id/attendance/{completed.ActivityId}", null), HttpStatusCode.NoContent);
        var list = await admin.GetFromJsonAsync<MemberActivityListDto>("/api/admin/members/member-id/activities");
        Assert.Equal(1, list!.TotalCount); Assert.True(Assert.Single(list.Items).IsAttended);
        var members = await admin.GetFromJsonAsync<List<AttendanceMemberDto>>($"/api/admin/activities/app:{completed.ActivityId}/members");
        Assert.True(members!.Single(m => m.UserId == "member-id").IsAttended);
        await Status(SendDelete(admin, $"/api/admin/members/member-id/attendance/{completed.ActivityId}", new AttendanceChangeRequest()), HttpStatusCode.NoContent);
        await Status(admin.DeleteAsync($"/api/admin/activities/app:{completed.ActivityId}"), HttpStatusCode.NoContent);
        await Status(admin.DeleteAsync($"/api/admin/activities/app:{completed.ActivityId}"), HttpStatusCode.NotFound); // 앱 키 재시도
        await Status(admin.DeleteAsync($"/api/admin/activities/{row.Key}"), HttpStatusCode.NoContent); // 전환된 마크의 재시도는 no-op
        using var verifyScope = host.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await verify.AuditLogs.CountAsync(a => a.Action == AdminAction.ActivityCancel && a.TargetType == "Activity"));
        Assert.Empty(await verify.DayMarks.ToListAsync());
    }

    private sealed class PasswordChangeHandler : HttpMessageHandler
    {
        private bool changed;
        public int TokenRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var route = request.RequestUri!.AbsolutePath;
            HttpResponseMessage response;
            if (route == "/api/auth/csrf")
            {
                TokenRequests++;
                response = new(HttpStatusCode.OK) { Content = JsonContent.Create(new CsrfTokenDto(changed ? "after" : "before")) };
            }
            else if (route == "/api/my/profile/password" && request.Method == HttpMethod.Put)
            {
                changed = true;
                response = new(HttpStatusCode.NoContent);
            }
            else if (route == "/api/auth/user")
            {
                response = changed ? new(HttpStatusCode.Unauthorized) : new(HttpStatusCode.OK)
                { Content = JsonContent.Create(new SessionUserDto("member-id", "member", ["MEMBER"])) };
            }
            else throw new InvalidOperationException("Unexpected browser transport request.");
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task Browser_Transport_Handles_Null_Dtos_And_Preserves_Error_Types()
    {
        using var handler = new ResponseHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://localhost") };
        var api = new VolleyballClub.Client.Services.ApiHttp(http);
        Assert.Null(await api.GetNullableAsync<FeePeriodDto>("empty"));
        handler.Status = HttpStatusCode.NotFound;
        await Assert.ThrowsAsync<VolleyballClub.Application.Exceptions.NotFoundException>(() => api.GetAsync<object>("missing"));
        handler.Status = HttpStatusCode.Conflict;
        await Assert.ThrowsAsync<VolleyballClub.Application.Exceptions.ConflictException>(() => api.GetAsync<object>("conflict"));
        handler.Status = HttpStatusCode.Unauthorized;
        var cleared = false;
        api.Unauthorized += () => cleared = true;
        await Assert.ThrowsAsync<VolleyballClub.Application.Exceptions.ForbiddenException>(() => api.GetAsync<object>("session"));
        Assert.True(cleared);
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = JsonContent.Create(new ApiErrorDto("test", "test error"))
            });
    }

    private static async Task Login(HttpClient client, string name)
    {
        await Token(client);
        await Status(client.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, "pass")), HttpStatusCode.NoContent);
    }
    private static async Task Token(HttpClient client)
    {
        var token = (await client.GetFromJsonAsync<CsrfTokenDto>("/api/auth/csrf"))!.Token;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
    }
    private static Task<HttpResponseMessage> SendDelete(HttpClient client, string path, object body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, path) { Content = JsonContent.Create(body) });
    private static async Task Status(Task<HttpResponseMessage> task, HttpStatusCode expected)
    {
        using var response = await task;
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
    private static async Task<T> Read<T>(Task<HttpResponseMessage> task)
    {
        using var response = await task;
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed class ApiHost : WebApplicationFactory<Program>
    {
        private readonly string dbPath = Path.Combine(Path.GetTempPath(), $"vicikyu-http-{Guid.NewGuid():N}.db");
        public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={dbPath}");
        }
        public static async Task<ApiHost> StartAsync()
        {
            var host = new ApiHost();
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            foreach (var role in new[] { "ADMIN", "MEMBER" }) Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
            foreach (var (name, role) in new[] { ("admin", "ADMIN"), ("member", "MEMBER") })
            {
                var user = new ApplicationUser { Id = name + "-id", UserName = name, Name = name, StudentNumber = name, Department = "test", Status = MemberStatus.Active };
                Assert.True((await users.CreateAsync(user, "pass")).Succeeded);
                Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
            }
            return host;
        }
        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in new[] { dbPath, dbPath + "-shm", dbPath + "-wal" }) if (File.Exists(path)) File.Delete(path);
        }
    }
}
