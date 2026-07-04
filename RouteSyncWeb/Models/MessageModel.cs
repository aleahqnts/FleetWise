#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWise.Models;

[Table("messages")]
public class Message : BaseModel
{
    [PrimaryKey("message_id")]
    public int MessageId { get; set; }

    [Column("sender_id")]
    public int SenderId { get; set; }

    [Column("target_audience")]
    public string TargetAudience { get; set; }

    [Column("target_id")]
    public string TargetId { get; set; }

    [Column("subject")]
    public string Subject { get; set; }

    [Column("body")]
    public string Body { get; set; }

    [Column("priority")]
    public string Priority { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}