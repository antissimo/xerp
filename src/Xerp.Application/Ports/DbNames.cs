namespace Xerp.Application.Ports;

/// <summary>
/// Names shared between Application queries and the Infrastructure model.
/// </summary>
public static class DbNames
{
    /// <summary>
    /// Shadow property present on every entity with a code: the database-generated lower-case form
    /// of the code (<c>lower("Code")</c>). Uniqueness, lookup and ordering by code use it, so
    /// case-insensitivity is decided by the database alone.
    /// </summary>
    public const string CodeLower = "CodeLower";

    public const string TenantCodeIndex = "IX_Tenants_CodeLower";
    public const string UnitOfMeasureCodeIndex = "IX_UnitsOfMeasure_TenantId_CodeLower";
    public const string ArticleCodeIndex = "IX_Articles_TenantId_CodeLower";

    public const string ArticleBaseUnitForeignKey = "FK_Articles_UnitsOfMeasure_TenantId_BaseUnitId";

    /// <summary>Spec 011, R2: the partial unique index that allows one default warehouse per tenant.</summary>
    public const string DefaultWarehouseIndex = "IX_Warehouses_TenantId_Default";
    public const string DefaultWarehouseIsActiveCheck = "CK_Warehouses_DefaultIsActive";
}

/// <summary>Functions that are evaluated by the database, never in memory.</summary>
public static class DbText
{
    /// <summary>SQL <c>lower(text)</c>. Usable only inside a query on <see cref="IXerpDb"/>.</summary>
    public static string Lower(string value) =>
        throw new InvalidOperationException("DbText.Lower can only be used inside a database query.");
}
