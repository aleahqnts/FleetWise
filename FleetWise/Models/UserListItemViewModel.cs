namespace FleetWise.Models
{
    public class UserListItemViewModel
    {
        public int UserId { get; set; }

        /// <summary>Formatted as "Last, First M."</summary>
        public string FullName { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string RoleName { get; set; } = string.Empty;

        public string AccountStatus { get; set; } = string.Empty;
    }
}
