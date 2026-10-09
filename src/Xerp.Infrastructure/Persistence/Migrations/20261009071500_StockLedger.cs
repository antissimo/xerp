using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xerp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Articles_TenantId_Id",
                table: "Articles",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "DocumentCounters",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastNumber = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentCounters", x => new { x.TenantId, x.DocumentType });
                    table.ForeignKey(
                        name: "FK_DocumentCounters_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PostedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockDocuments", x => x.Id);
                    table.UniqueConstraint("AK_StockDocuments_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_StockDocuments_ApiKeys_TenantId_CreatedBy",
                        columns: x => new { x.TenantId, x.CreatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockDocuments_ApiKeys_TenantId_PostedBy",
                        columns: x => new { x.TenantId, x.PostedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockDocuments_ApiKeys_TenantId_UpdatedBy",
                        columns: x => new { x.TenantId, x.UpdatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockDocuments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockDocuments_Warehouses_TenantId_WarehouseId",
                        columns: x => new { x.TenantId, x.WarehouseId },
                        principalTable: "Warehouses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockDocumentLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockDocumentLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockDocumentLines_Articles_TenantId_ArticleId",
                        columns: x => new { x.TenantId, x.ArticleId },
                        principalTable: "Articles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockDocumentLines_StockDocuments_TenantId_DocumentId",
                        columns: x => new { x.TenantId, x.DocumentId },
                        principalTable: "StockDocuments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockDocumentLines_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    DocumentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    PostedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PostedBy = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockLedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockLedgerEntries_ApiKeys_TenantId_PostedBy",
                        columns: x => new { x.TenantId, x.PostedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockLedgerEntries_Articles_TenantId_ArticleId",
                        columns: x => new { x.TenantId, x.ArticleId },
                        principalTable: "Articles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockLedgerEntries_StockDocuments_TenantId_DocumentId",
                        columns: x => new { x.TenantId, x.DocumentId },
                        principalTable: "StockDocuments",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockLedgerEntries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockLedgerEntries_Warehouses_TenantId_WarehouseId",
                        columns: x => new { x.TenantId, x.WarehouseId },
                        principalTable: "Warehouses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocumentLines_TenantId_ArticleId",
                table: "StockDocumentLines",
                columns: new[] { "TenantId", "ArticleId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocumentLines_TenantId_DocumentId_LineNo",
                table: "StockDocumentLines",
                columns: new[] { "TenantId", "DocumentId", "LineNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_CreatedBy",
                table: "StockDocuments",
                columns: new[] { "TenantId", "CreatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_Number",
                table: "StockDocuments",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_PostedBy",
                table: "StockDocuments",
                columns: new[] { "TenantId", "PostedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_UpdatedBy",
                table: "StockDocuments",
                columns: new[] { "TenantId", "UpdatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_WarehouseId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLedgerEntries_TenantId_ArticleId_WarehouseId",
                table: "StockLedgerEntries",
                columns: new[] { "TenantId", "ArticleId", "WarehouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLedgerEntries_TenantId_DocumentId_LineNo",
                table: "StockLedgerEntries",
                columns: new[] { "TenantId", "DocumentId", "LineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLedgerEntries_TenantId_PostedBy",
                table: "StockLedgerEntries",
                columns: new[] { "TenantId", "PostedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_StockLedgerEntries_TenantId_WarehouseId",
                table: "StockLedgerEntries",
                columns: new[] { "TenantId", "WarehouseId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentCounters");

            migrationBuilder.DropTable(
                name: "StockDocumentLines");

            migrationBuilder.DropTable(
                name: "StockLedgerEntries");

            migrationBuilder.DropTable(
                name: "StockDocuments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Articles_TenantId_Id",
                table: "Articles");
        }
    }
}
