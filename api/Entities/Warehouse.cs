using System.ComponentModel.DataAnnotations;

namespace Xerp.Api.Entities;

public class Warehouse : Entity
{
    [MaxLength(200)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? City { get; set; }
}

public class WarehouseInput : EntityInput
{
    [StringLength(200)]
    public string? Address { get; set; }

    [StringLength(100)]
    public string? City { get; set; }
}
