using System.ComponentModel.DataAnnotations;

namespace FleetWise.Models
{
    public class RoleFormViewModel
    {
        // null = Add Role (CreateRole), non-null = Edit Role (UpdateRole)
        public int? RoleId { get; set; }

        [Required, StringLength(50)]
        public string RoleName { get; set; } = string.Empty;

        [Required, StringLength(50)]
        public string AccessLevel { get; set; } = string.Empty;

        // Bound from hidden+checkbox pairs: WebPermissions[Dashboard], WebPermissions[FleetMap], ...
        public Dictionary<string, bool> WebPermissions { get; set; } = new();

        // Bound from MobilePermissions[FullAccess]
        public Dictionary<string, bool> MobilePermissions { get; set; } = new();
    }
}
