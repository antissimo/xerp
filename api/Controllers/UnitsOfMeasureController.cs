using Microsoft.AspNetCore.Mvc;
using Xerp.Api.Data;
using Xerp.Api.Entities;

namespace Xerp.Api.Controllers;

[Route("units-of-measure")]
public class UnitsOfMeasureController(AppDb db) : CrudController<UnitOfMeasure, UnitOfMeasureInput>(db);
