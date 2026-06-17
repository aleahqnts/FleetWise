#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("messages")]
public class MessageModel : BaseModel
{
    [PrimaryKey("message_id", false)]
    public long MessageId { get; set; }

    [Column("sender_id")]
    public int SenderId { get; set; }

    // "all" = broadcast, "route" = per route (target_id = route_id),
    // "driver" = per driver (target_id = user_id). Only "driver" tracks is_read.
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

    [Column("is_read")]
    public bool IsRead { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
