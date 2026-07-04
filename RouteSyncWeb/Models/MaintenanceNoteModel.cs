#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

// One entry in a maintenance incident's audit thread: an admin comment or an action
// (Out of Service / Returned to Service / Resolved) taken on a maintenance_log. Gives the
// flagged-vehicle review its history — who did what, when, and why.
[Table("maintenance_notes")]
public class MaintenanceNote : BaseModel
{
    // shouldInsert: false -> DB identity generates the id on insert.
    [PrimaryKey("note_id", false)]
    public long NoteId { get; set; }

    [Column("log_id")]
    public int LogId { get; set; }

    [Column("author_id")]
    public int? AuthorId { get; set; }

    [Column("author_name")]
    public string AuthorName { get; set; }

    // 'Comment' | 'Out of Service' | 'Returned to Service' | 'Resolved'
    [Column("action")]
    public string Action { get; set; }

    [Column("note")]
    public string Note { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
