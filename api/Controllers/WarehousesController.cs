using Microsoft.AspNetCore.Mvc;
using Xerp.Api.Data;
using Xerp.Api.Entities;

namespace Xerp.Api.Controllers;

[Route("warehouses")]
public class WarehousesController(AppDb db) : CrudController<Warehouse, WarehouseInput>(db)
{
    protected override void Apply(WarehouseInput input, Warehouse entity)
    {
        base.Apply(input, entity);
        entity.Address = input.Address;
        entity.City = input.City;
    }
}
