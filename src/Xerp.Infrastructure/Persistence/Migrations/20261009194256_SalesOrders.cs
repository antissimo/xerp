using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Xerp.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SalesOrderId",
                table: "StockDocuments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalesOrderId",
                table: "StockDocumentLines",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    RequestedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_SalesOrders", x => x.Id);
                    table.UniqueConstraint("AK_SalesOrders_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_SalesOrders_ApiKeys_TenantId_ClosedBy",
                        columns: x => new { x.TenantId, x.ClosedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_ApiKeys_TenantId_ConfirmedBy",
                        columns: x => new { x.TenantId, x.ConfirmedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_ApiKeys_TenantId_CreatedBy",
                        columns: x => new { x.TenantId, x.CreatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_ApiKeys_TenantId_UpdatedBy",
                        columns: x => new { x.TenantId, x.UpdatedBy },
                        principalTable: "ApiKeys",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Partners_TenantId_CustomerId",
                        columns: x => new { x.TenantId, x.CustomerId },
                        principalTable: "Partners",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Warehouses_TenantId_WarehouseId",
                        columns: x => new { x.TenantId, x.WarehouseId },
                        principalTable: "Warehouses",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderLines",
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
                    DeliveredBaseQuantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderLines", x => x.Id);
                    table.UniqueConstraint("AK_SalesOrderLines_TenantId_OrderId_LineNo", x => new { x.TenantId, x.OrderId, x.LineNo });
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_Articles_TenantId_ArticleId",
                        columns: x => new { x.TenantId, x.ArticleId },
                        principalTable: "Articles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_SalesOrders_TenantId_OrderId",
                        columns: x => new { x.TenantId, x.OrderId },
                        principalTable: "SalesOrders",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_UnitsOfMeasure_TenantId_UnitId",
                        columns: x => new { x.TenantId, x.UnitId },
                        principalTable: "UnitsOfMeasure",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocuments_TenantId_SalesOrderId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockDocumentLines_TenantId_SalesOrderId_OrderLineNo",
                table: "StockDocumentLines",
                columns: new[] { "TenantId", "SalesOrderId", "OrderLineNo" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_TenantId_ArticleId",
                table: "SalesOrderLines",
                columns: new[] { "TenantId", "ArticleId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_TenantId_UnitId",
                table: "SalesOrderLines",
                columns: new[] { "TenantId", "UnitId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_TenantId_ClosedBy",
                table: "SalesOrders",
                columns: new[] { "TenantId", "ClosedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_TenantId_ConfirmedBy",
                table: "SalesOrders",
                columns: new[] { "TenantId", "ConfirmedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_TenantId_CreatedBy",
                table: "SalesOrders",
                columns: new[] { "TenantId", "CreatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_TenantId_CustomerId",
                table: "SalesOrders",
                columns: new[] { "TenantId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_TenantId_Number",
                table: "SalesOrders",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_TenantId_UpdatedBy",
                table: "SalesOrders",
                columns: new[] { "TenantId", "UpdatedBy" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_TenantId_WarehouseId",
                table: "SalesOrders",
                columns: new[] { "TenantId", "WarehouseId" });

            migrationBuilder.AddForeignKey(
                name: "FK_StockDocumentLines_SalesOrderLines_OrderLine",
                table: "StockDocumentLines",
                columns: new[] { "TenantId", "SalesOrderId", "OrderLineNo" },
                principalTable: "SalesOrderLines",
                principalColumns: new[] { "TenantId", "OrderId", "LineNo" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_StockDocuments_SalesOrders_TenantId_SalesOrderId",
                table: "StockDocuments",
                columns: new[] { "TenantId", "SalesOrderId" },
                principalTable: "SalesOrders",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockDocumentLines_SalesOrderLines_OrderLine",
                table: "StockDocumentLines");

            migrationBuilder.DropForeignKey(
                name: "FK_StockDocuments_SalesOrders_TenantId_SalesOrderId",
                table: "StockDocuments");

            migrationBuilder.DropTable(
                name: "SalesOrderLines");

            migrationBuilder.DropTable(
                name: "SalesOrders");

            migrationBuilder.DropIndex(
                name: "IX_StockDocuments_TenantId_SalesOrderId",
                table: "StockDocuments");

            migrationBuilder.DropIndex(
                name: "IX_StockDocumentLines_TenantId_SalesOrderId_OrderLineNo",
                table: "StockDocumentLines");

            migrationBuilder.DropColumn(
                name: "SalesOrderId",
                table: "StockDocuments");

            migrationBuilder.DropColumn(
                name: "SalesOrderId",
                table: "StockDocumentLines");
        }
    }
}
