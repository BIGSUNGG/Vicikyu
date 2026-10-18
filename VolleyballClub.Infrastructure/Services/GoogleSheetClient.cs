using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using VolleyballClub.Application.Abstractions;
using VolleyballClub.Application.Dtos;
using VolleyballClub.Infrastructure.Persistence;

namespace VolleyballClub.Infrastructure.Services;

/// <summary>Google Sheets API 기반 ISheetClient 구현.
/// 자격증명(서비스 계정 JSON 본문 또는 파일 경로)은 Setting 테이블의 Google:ServiceAccountJson 값을
/// 우선 사용하고, 없으면 구성(개발 user-secrets) 값으로 폴백한다 — 값이 바뀌면 서비스를 재구성한다.</summary>
public sealed partial class GoogleSheetClient(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IConfiguration configuration) : ISheetClient
{
    public const string CredentialsSettingKey = "Google:ServiceAccountJson";

    private readonly SemaphoreSlim gate = new(1, 1);
    private SheetsService? service;
    private string? serviceCredentials;

    // Sheets API 분당 할당량(읽기·쓰기 각 60/사용자)을 넘기지 않도록 전 요청을 직렬화해 간격을 둔다.
    // 첫 전체 동기화는 9탭 × (탭 읽기+원본 바인드 검증) ≈ 50읽기라 순차 실행만으로도 한도 안에 든다.
    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static DateTimeOffset lastRequestUtc;
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromSeconds(1.3);

    private static async Task ThrottleAsync(CancellationToken ct)
    {
        await RequestGate.WaitAsync(ct);
        try
        {
            var wait = lastRequestUtc + MinimumRequestInterval - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            lastRequestUtc = DateTimeOffset.UtcNow;
        }
        finally { RequestGate.Release(); }
    }

    public async Task<IReadOnlyList<SheetTab>> GetTabsAsync(
        string spreadsheetId, IEnumerable<string>? tabNames = null, CancellationToken ct = default)
    {
        var svc = await GetServiceAsync(ct);
        await ThrottleAsync(ct);
        var names = tabNames is null
            ? (await GetSheetPropertiesAsync(svc, spreadsheetId, fields: "sheets(properties(title))", ct))
                .Select(p => p.Title!.ToString())
                .ToList()
            : tabNames.ToList();
        if (names.Count == 0)
        {
            return [];
        }

        var batch = svc.Spreadsheets.Values.BatchGet(spreadsheetId);
        batch.Ranges = names;
        await ThrottleAsync(ct);
        var response = await batch.ExecuteAsync(ct);

        // BatchGet은 요청 순서대로 반환한다. 빈 탭은 Values가 null로 온다.
        return names.Select((name, i) => new SheetTab(
                name,
                ToRows(response.ValueRanges is not null && i < response.ValueRanges.Count
                    ? response.ValueRanges[i].Values
                    : null)))
            .ToList();
    }

    public async Task WriteColumnAsync(
        string spreadsheetId, string tabName, int columnIndex, SheetColumn column, CancellationToken ct = default)
    {
        if (column.Cells.Count == 0)
        {
            return;
        }

        var svc = await GetServiceAsync(ct);
        var letter = ColumnLetter(columnIndex);
        var lastRow = column.FirstRowIndex + column.Cells.Count - 1;
        var range = $"{QuoteTab(tabName)}!{letter}{column.FirstRowIndex}:{letter}{lastRow}";

        var update = svc.Spreadsheets.Values.Update(
            new ValueRange
            {
                Values = column.Cells.Select(cell => (IList<object>)new List<object> { cell }).ToList(),
            },
            spreadsheetId,
            range);
        // '='로 시작하는 셀(총 참여자 수 수식)은 USER_ENTERED로 써야 함수로 동작한다 — RAW면 텍스트로 박힌다.
        // 나머지(출석 마크·상태·라벨)는 RAW 유지 — USER_ENTERED는 날짜형 헤더 등을 오인 파싱할 수 있다.
        update.ValueInputOption = column.Cells.Any(c => c.StartsWith("="))
            ? SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED
            : SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
        await update.ExecuteAsync(ct);
    }

    public async Task InsertColumnAsync(
        string spreadsheetId, string tabName, int columnIndex, SheetColumn column, CancellationToken ct = default)
    {
        var svc = await GetServiceAsync(ct);
        var sheetId = (await GetSheetPropertiesAsync(
                svc,
                spreadsheetId,
                fields: "sheets(properties(sheetId,title))",
                ct))
            .FirstOrDefault(p => p.Title == tabName)?.SheetId
            ?? throw new InvalidOperationException($"시트에서 탭 '{tabName}'을(를) 찾을 수 없습니다.");

        var request = new BatchUpdateSpreadsheetRequest
        {
            Requests =
            [
                new Request
                {
                    InsertDimension = new InsertDimensionRequest
                    {
                        Range = new DimensionRange
                        {
                            SheetId = sheetId,
                            Dimension = "COLUMNS",
                            StartIndex = columnIndex - 1, // 0-based, 반 닫힌 구간
                            EndIndex = columnIndex,
                        },
                        // 왼쪽(이전 날짜) 열 서식을 상속해 날짜 열 서식을 유지한다
                        InheritFromBefore = true,
                    },
                },
            ],
        };
        await svc.Spreadsheets.BatchUpdate(request, spreadsheetId).ExecuteAsync(ct);

        await WriteColumnAsync(spreadsheetId, tabName, columnIndex, column, ct);
    }

    public async Task DeleteColumnAsync(
        string spreadsheetId, string tabName, int columnIndex, CancellationToken ct = default)
    {
        var svc = await GetServiceAsync(ct);
        var sheetId = (await GetSheetPropertiesAsync(
                svc,
                spreadsheetId,
                fields: "sheets(properties(sheetId,title))",
                ct))
            .FirstOrDefault(p => p.Title == tabName)?.SheetId
            ?? throw new InvalidOperationException($"시트에서 탭 '{tabName}'을(를) 찾을 수 없습니다.");

        var request = new BatchUpdateSpreadsheetRequest
        {
            Requests =
            [
                new Request
                {
                    DeleteDimension = new DeleteDimensionRequest
                    {
                        Range = new DimensionRange
                        {
                            SheetId = sheetId,
                            Dimension = "COLUMNS",
                            StartIndex = columnIndex - 1, // 0-based, 반 닫힌 구간
                            EndIndex = columnIndex,
                        },
                    },
                },
            ],
        };
        await svc.Spreadsheets.BatchUpdate(request, spreadsheetId).ExecuteAsync(ct);
    }

    private async Task<SheetsService> GetServiceAsync(CancellationToken ct)
    {
        var credentials = await GetCurrentCredentialsAsync(ct);
        if (service is not null && serviceCredentials == credentials)
        {
            return service;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (service is not null && serviceCredentials == credentials)
            {
                return service;
            }

            var credential = credentials.TrimStart().StartsWith('{')
                ? CredentialFactory.FromJson<ServiceAccountCredential>(credentials).ToGoogleCredential()
                : (await CredentialFactory.FromFileAsync<ServiceAccountCredential>(credentials, ct)).ToGoogleCredential();
            service?.Dispose();
            service = new SheetsService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential.CreateScoped(SheetsService.Scope.Spreadsheets),
            });
            serviceCredentials = credentials;
            return service;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> GetCurrentCredentialsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var fromSetting = await db.Settings
            .Where(s => s.Key == CredentialsSettingKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return fromSetting
            ?? configuration["Google:ServiceAccountJson"]
            ?? throw new InvalidOperationException(
                "구글 시트 자격증명이 없습니다. 관리자 설정의 서비스 계정 키 또는 user-secrets 'Google:ServiceAccountJson'을 확인하세요.");
    }

    private static async Task<IList<SheetProperties>> GetSheetPropertiesAsync(
        SheetsService svc, string spreadsheetId, string fields, CancellationToken ct)
    {
        var request = svc.Spreadsheets.Get(spreadsheetId);
        request.Fields = fields;
        await ThrottleAsync(ct);
        var spreadsheet = await request.ExecuteAsync(ct);
        return spreadsheet.Sheets.Select(sheet => sheet.Properties).ToList();
    }

    private static IReadOnlyList<IReadOnlyList<string>> ToRows(IList<IList<object>>? values) =>
        values?.Select(row => (IReadOnlyList<string>)row.Select(cell => cell?.ToString() ?? "").ToList()).ToList()
            ?? [];

    private static string QuoteTab(string tabName) => $"'{tabName.Replace("'", "''")}'";

    private static string ColumnLetter(int column)
    {
        var letters = "";
        while (column > 0)
        {
            letters = (char)('A' + (column - 1) % 26) + letters;
            column = (column - 1) / 26;
        }

        return letters;
    }
}
