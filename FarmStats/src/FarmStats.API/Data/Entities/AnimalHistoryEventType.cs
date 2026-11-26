namespace FarmStats.API.Data.Entities;

public enum AnimalHistoryEventType
{
    Created = 0,
    StatusChanged = 1,
    Deleted = 2,
    Restored = 3,
    QuantityChanged = 4
}