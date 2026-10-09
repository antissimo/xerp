using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xerp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchaseOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseOrderId",
                table: "StockDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrderLineNo",
                table: "StockDocumentLines",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseOrderId",
                table: "StockDocumentLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpectedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrders", x => x.Id);
                    table.UniqueConstraint("AK_PurchaseOrders_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_ApiKeys_TenantId_ClosedBy",
                        columns: x => new { x.TenantId, x.ClosedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_ApiKeys_TenantId_ConfirmedBy",
                        columns: x => new { x.TenantId, x.ConfirmedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_ApiKeys_TenantId_CreatedBy",
                        columns: x => new { x.TenantId, x.CreatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_ApiKeys_TenantId_UpdatedBy",
                        columns: x => new { x.TenantId, x.UpdatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Partners_TenantId_SupplierId",
                        columns: x => new { x.TenantId, x.SupplierId },
                        principalTable: "Partners",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Warehouses_TenantId_WarehouseId",
                        columns: x => new { x.TenantId, x.WarehouseId },
                        principalTable: "Warehouses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNo = table.Column<int>(type: "integer", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Factor = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    BaseQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    ReceivedBaseQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLines", x => x.Id);
                    table.UniqueConstraint("AK_PurchaseOrderLines_TenantId_OrderId_LineNo", x => new { x.TenantId, x.OrderId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_Articles_TenantId_ArticleId",
                        columns: x => new { x.TenantId, x.ArticleId },
                        principalTable: "Articles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_PurchaseOrders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalTable: "PurchaseOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_UnitsOfMeasure_TenantId_UnitId",
                        columns: x => new { x.TenantId, x.UnitId },
                        principalTable: "UnitsOfMeasure",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_PurchaseOrderId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "PurchaseOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocumentLines_TenantId_PurchaseOrderId_OrderLineNo",
                table: "StockDocumentLines",
                columns: new[] { "TenantId", "PurchaseOrderId", "OrderLineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_TenantId_ArticleId",
                table: "PurchaseOrderLines",
                columns: new[] { "TenantId", "ArticleId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_TenantId_UnitId",
                table: "PurchaseOrderLines",
                columns: new[] { "TenantId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_TenantId_ClosedBy",
                table: "PurchaseOrders",
                columns: new[] { "TenantId", "ClosedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_TenantId_ConfirmedBy",
                table: "PurchaseOrders",
                columns: new[] { "TenantId", "ConfirmedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_TenantId_CreatedBy",
                table: "PurchaseOrders",
                columns: new[] { "TenantId", "CreatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_TenantId_Number",
                table: "PurchaseOrders",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_TenantId_SupplierId",
                table: "PurchaseOrders",
                columns: new[] { "TenantId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_TenantId_UpdatedBy",
                table: "PurchaseOrders",
                columns: new[] { "TenantId", "UpdatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_TenantId_WarehouseId",
                table: "PurchaseOrders",
                columns: new[] { "TenantId", "WarehouseId" });

            migrationBuilder.AddForeignKey(
                name: "FK_StockDocumentLines_PurchaseOrderLines_OrderLine",
                table: "StockDocumentLines",
                columns: new[] { "TenantId", "PurchaseOrderId", "OrderLineNo" },
                principalTable: "PurchaseOrderLines",
                principalColumns: new[] { "TenantId", "OrderId", "LineNo" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockDocuments_PurchaseOrders_TenantId_PurchaseOrderId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "PurchaseOrderId" },
                principalTable: "PurchaseOrders",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockDocumentLines_PurchaseOrderLines_OrderLine",
                table: "StockDocumentLines");

            migrationBuilder.DropForeignKey(
                name: "FK_StockDocuments_PurchaseOrders_TenantId_PurchaseOrderId",
                table: "StockDocuments");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLines");

            migrationBuilder.DropTable(
                name: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_StockDocuments_TenantId_PurchaseOrderId",
                table: "StockDocuments");

            migrationBuilder.DropIndex(
                name: "IX_StockDocumentLines_TenantId_PurchaseOrderId_OrderLineNo",
                table: "StockDocumentLines");

            migrationBuilder.DropColumn(
                name: "PurchaseOrderId",
                table: "StockDocuments");

            migrationBuilder.DropColumn(
                name: "OrderLineNo",
                table: "StockDocumentLines");

            migrationBuilder.DropColumn(
                name: "PurchaseOrderId",
                table: "StockDocumentLines");
        }
    }
}
