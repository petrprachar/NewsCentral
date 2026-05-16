namespace NewsCentral.Models;

public class Schedule : IEntity
{
    public string ScheduleID { get; set; } = Guid.NewGuid().ToString();
    public string PresentationID { get; set; } = string.Empty;
    public string Version { get; set; } = "1";
    public DateTime DateCreated { get; set; } = DateTime.UtcNow;
    public DateTime ScheduleCreated { get; set; } = DateTime.UtcNow;
    public DateTime ScheduleStart { get; set; } = DateTime.MinValue;
    public DateTime ScheduleEnd { get; set; } = DateTime.MaxValue;
    public string DaysOfWeek { get; set; } = "1,2,3,4,5,6,7"; // Mon-Sun default
    public bool IsActive { get; set; } = true;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime LastModified { get; set; } = DateTime.UtcNow;

    public string GetId() => ScheduleID;
    public void SetId(string id) => ScheduleID = id;
}