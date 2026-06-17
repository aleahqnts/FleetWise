#nullable disable
using Postgrest.Attributes;
using Postgrest.Models;

namespace FleetWiseMobile.Models;

[Table("notifications")]
public class NotificationModel : BaseModel
{
    [PrimaryKey("notification_id", false)]
    public long NotificationId { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("type")]
    public string Type { get; set; }

    [Column("title")]
    public string Title { get; set; }

    [Column("body")]
    public string Body { get; set; }

    [Column("is_read")]
    public bool IsRead { get; set; }

    [Column("trip_id")]
    public string TripId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
