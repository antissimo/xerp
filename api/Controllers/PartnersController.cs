using Microsoft.AspNetCore.Mvc;
using Xerp.Api.Data;
using Xerp.Api.Entities;

namespace Xerp.Api.Controllers;

[Route("partners")]
public class PartnersController(AppDb db) : CrudController<Partner, PartnerInput>(db)
{
    protected override void Apply(PartnerInput input, Partner entity)
    {
        base.Apply(input, entity);
        entity.TaxId = input.TaxId;
        entity.Address = input.Address;
        entity.City = input.City;
        entity.PostalCode = input.PostalCode;
        entity.CountryCode = input.CountryCode?.ToUpperInvariant();
        entity.Email = input.Email;
        entity.Phone = input.Phone;
    }
}
