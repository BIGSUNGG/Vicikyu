using VolleyballClub.Domain.Enums;
namespace VolleyballClub.Domain.Entities;

/// <summary>Stable sheet-column identity, also used for scheduled/non-activity dates.</summary>
public class ActivitySource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int SheetDefinitionId { get; set; }
    public string TabName { get; set; } = "";
    public DateOnly Date { get; set; }
    public SheetDayKind Kind { get; set; }
    public string? Reason { get; set; }
    public bool IsMissing { get; set; }
    public int? ActivityId { get; set; }
    public Activity? Activity { get; set; }
}

/// <summary>Durable preimage: a remaining journal means the database mutation did not commit.</summary>
public class SheetEditJournal
{
    public string Id { get; set; } = "";
    public string SpreadsheetId { get; set; } = "";
    public string BeforeJson { get; set; } = "";
    public string MutationJson { get; set; } = "";
}
