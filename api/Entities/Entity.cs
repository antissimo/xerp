using System.ComponentModel.DataAnnotations;

namespace Xerp.Api.Entities;

/// <summary>Columns every basic entity has.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Human-readable key, unique per table (e.g. "ART-001", "kg").</summary>
    [MaxLength(50)]
    public string Code { get; set; } = "";

    [MaxLength(200)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Fields a client may set on any basic entity.</summary>
public abstract class EntityInput
{
    [Required, StringLength(50, MinimumLength = 1)]
    public string Code { get; set; } = "";

    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;
}
