# Plan i stanje

Glavni pregled petlje arhitekt -> tester -> builder -> review -> merge. Redoslijed koraka dolazi iz
`docs/roadmap.md` (piše ga arhitekt); ovaj dokument vodi orkestrator i ažurira ga nakon svakog koraka.

Zadnje ažuriranje: 2026-10-09 10:10

## Opseg MVP-a

Odluka vlasnika (2026-10-09): rad se ograničava na MVP. Granica (prijedlog orkestratora, unesena i u `docs/roadmap.md`; vlasnik je može pomaknuti):
**MVP = specovi 001–010** — matični podaci, MCP sučelje, zalihe (primke, izdatnice, međuskladišnice, storno,
preračun jedinica, inventura), nabava i prodaja. Sve od 011 nadalje (vrednovanje, računovodstvo, računi,
plaćanja, platforma) je nakon MVP-a i petlja ga ne radi dok vlasnik ne kaže.

## Koraci po specifikaciji

Svaki spec prolazi pet koraka:

1. **Spec** — arhitekt napiše `docs/specs/NNN-*.md` (i ADR ako treba).
2. **Testovi** — tester napiše black-box acceptance testove na grani `tests/NNN-*`.
3. **Kod** — builder implementira na grani `feat/NNN-*` dok svi testovi ne prođu.
4. **Review** — arhitekt napiše `docs/reviews/NNN.md` s presudom OK.
5. **Merge** — orkestrator spoji granu u `main` i pusha na GitHub.

Oznake: ✅ gotovo · 🔄 u tijeku · ⬜ nije započeto · — ne primjenjuje se

| # | Spec | Spec | Testovi | Kod | Review | Merge |
|---|---|:-:|:-:|:-:|:-:|:-:|
| 001 | Temelj + jedinice mjere (slojevi, tenanti, API ključevi, model grešaka) | ✅ | ✅ | ✅ | ✅ | ✅ |
| 002 | Artikli (matični podaci artikala) | ✅ | ✅ | ✅ | ✅ | ✅ |
| 003 | MCP server + upravljanje API ključevima | ✅ | ✅ | ✅ | ✅ | ✅ |
| 004 | Partneri i skladišta | ✅ | ✅ | ✅ | ✅ | ✅ |
| 005 | Skladišna knjiga, primke i izdatnice | ✅ | ✅ | ✅ | ✅ | ✅ |
| 006 | Međuskladišnice i storno | ✅ | ✅ | 🔄 | ⬜ | ⬜ |
| 007 | Preračun jedinica mjere po artiklu | ✅ | ✅ | ⬜ | ⬜ | ⬜ |
| 008 | Inventura / korekcija zaliha | ✅ | ✅ | ⬜ | ⬜ | ⬜ |
| 009 | Narudžbe dobavljačima -> primka robe | ✅ | ⬜ | ⬜ | ⬜ | ⬜ |
| 010 | Prodajne narudžbe -> isporuka | ✅ | ⬜ | ⬜ | ⬜ | ⬜ |
| | **— granica MVP-a — sve ispod je nakon MVP-a —** | | | | | |
| 011 | Vrednovanje zaliha | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 012 | Kontni plan + temeljnice | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 013 | Brojčane serije dokumenata | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 014 | Porezi + izlazni/ulazni računi | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 015 | Plaćanja i otvorene stavke | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 016 | Audit log *(platforma)* | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 017 | Dozvole po API ključu *(platforma)* | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 018 | OpenAPI dokument *(platforma)* | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 019 | Row-level security *(platforma)* | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |
| 020 | Rate limiting *(platforma)* | ⬜ | ⬜ | ⬜ | ⬜ | ⬜ |

Napomena: testove za 001 i 002 pisao je builder; od 003 nadalje piše ih tester.

## Što tko trenutno radi

- **Arhitekt:** čeka review 006 (namjerno bez zadatka zbog limita plana; čekaju ga pitanja testera za 007 i 008).
- **Builder:** implementacija speca 006 (grana `feat/006-stock-transfers-reversal`).
- **Tester:** čeka (limit plana na 95 %); sljedeće: testovi za 009 i 010.

## Dnevnik

- 2026-10-08 — spec 001 napisan; builder implementirao; review tražio 2 ispravka (NUL znak u nazivu -> 500,
  nepoznati query parametri).
- 2026-10-09 — 001 i 002 prošli review, spojeni u `main` i pushani (`5f5687d`).
- 2026-10-09 — dodan tester kao treći agent (`tester.md`); builder od 003 radi nad testerovim testovima.
- 2026-10-09 — roadmap presložen: prvo poslovna logika (zalihe, nabava, prodaja, računovodstvo), platforma na kraju.
- 2026-10-09 — specovi 003–008 napisani; testovi za 003, 004, 005 napisani; kod za 003 gotov
  (172 unit + 376 integracijskih testova prolazi, provjerio orkestrator).
- 2026-10-09 — kod za 004 gotov (builder javlja 234 unit + 632 integracijska testa, sve prolazi); čeka review.
- 2026-10-09 — 003 prošao review (OK), spojen u `main` i pushan.
- 2026-10-09 — 004 prošao review (OK; arhitekt sam pokrenuo 234 unit + 632 integracijska testa), spojen u `main` i pushan.
- 2026-10-09 — testovi za 006 napisani (`tests/006-stock-transfers-reversal`).
- 2026-10-09 — vlasnik ograničio opseg na MVP; lokalni model qwen3:4b isproban i odbačen (preslab).
- 2026-10-09 — kod za 005 gotov (skladišna knjiga); spec 009 (nabava) napisan.
- 2026-10-09 — spec 010 (prodaja) napisan; svi specovi MVP-a (001–010) su napisani.
- 2026-10-09 — 005 prošao review (OK; arhitekt pokrenuo 296 unit + 795 integracijskih testova), spojen u `main` i pushan.
- 2026-10-09 — specovi 007–010 usklađeni s izgrađenim kodom; napisan `docs/getting-started.md`.
- 2026-10-09 — testovi za 007 i 008 napisani. Limit plana na 95 % (reset 13:50); prednost ima builder na 006.

## Odluke vlasnika

Potvrđeno:
- Spec 003: nema MCP alata za kreiranje ključeva (samo HTTP); svaki ključ smije upravljati ključevima dok ne
  dođu dozvole; MCP klijenti samo sa statičkim API ključem; rate limiting kasnije.

Arhitektovi defaulti koji vrijede dok vlasnik ne kaže drugačije (detalji u ADR-ovima):
- Spec 004 (ADR-0011): porezni broj nije jedinstven; nema validacije po državama; jedna adresa po partneru;
  bez kontakt podataka; partner mora biti kupac ili dobavljač.
- Spec 005 (ADR-0012): negativna zaliha uvijek odbijena; provjera prema trenutnoj zalihi; fiksni brojevi
  `SR-`/`SI-`; količine do 6 decimala; ručne primke/izdatnice bez partnera i cijene; draft ne rezervira zalihu.
- Spec 006 (ADR-0013): prijenos je trenutan; storno je zaseban dokument iz iste serije, cijeli i konačan,
  odbijen ako bi zaliha otišla u minus; datum storna zadaje pozivatelj.
- Spec 007 (ADR-0014): vidi ADR.
- Spec 009/010 (ADR-0016, ADR-0017): potvrđena narudžba se ne mijenja (zatvori i napravi novu); nema
  primke/isporuke preko naručenog; primka/isporuka samo u skladištu narudžbe; samo artikli tipa roba; cijene u
  jednoj valuti, 2 decimale, bez poreza i popusta; rezervacija informira, ne blokira; nema ponude (draft
  narudžba služi kao ponuda); narudžbe se ne zatvaraju automatski.
- Spec 008 (ADR-0015): zastarjela inventura se odbija; inventura je djelomična; artikl jednom po inventuri;
  ništa se ne zamrzava dok je inventura otvorena; proknjižena inventura se može stornirati.
