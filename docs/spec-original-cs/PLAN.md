# PLAN – fáze a stav

> Agent tento soubor aktualizuje: po dokončení fáze ji odškrtne, doplní datum a krátkou poznámku (co vzniklo, odchylky od SPEC, otevřené otázky). Nová session začíná přečtením tohoto souboru.

## Fáze (každá končí zastavením a ukázkou)

- [x] **Fáze 1** – **Kostra:** repo, solution, Docker Compose (Postgres), CI (build + test + gitleaks), `.env.example`, generátor syntetických dat.
  - _Hotovo 2026-11-12._ .NET 10 (`PaymentMatching.slnx`, central package management, warnings as errors), generátor `src/PaymentMatching.DataGen` + CLI `tools/DataGen.Cli`, ukázková sada `data/sample` (seed 42, 300 plateb, 391 faktur, 60 odběratelů), 32 testů, CI (format, build, test, kontrola reprodukovatelnosti dat, gitleaks), Postgres 17 v Compose, ADR 0001–0002, `docs/data-format.md`.
  - _Odchylky od SPEC:_ SPEC neuvádí poměry případů – zvoleno 45/10/8/8/8/6/5/5/5 % (viz `docs/data-format.md`), lze změnit v `GeneratorOptions`. Jediná závislost je `xunit.v3` (testy).
  - _Doplněno po fázi:_ repo na GitHubu (soukromé, `stanislavmatzke-ops/payment-matching-ai`), CI zelené. Free Pascal poběží v Dockeru (`freepascal/fpc:3.2.2-full`, ověřeno přeložením testovacího programu v režimu `-Mdelphi`), nic se neinstaluje.
- [x] **Fáze 2** – **Legacy:** Delphi + PL/SQL modul, CLI přes Free Pascal, golden master výstupy.
  - _Hotovo 2026-11-12._ `legacy/delphi` (5 unit, překlad `fpc -Mdelphi` v Dockeru přes `legacy/run-legacy.sh`), `legacy/plsql` (package, jen zdroják), golden master `legacy/golden/` (300 + 1 000 plateb), CI job `legacy-golden-master`, ADR 0003, `docs/modernization.md` (krok 2 + výchozí čísla legacy).
  - _Výchozí stav legacy proti ground truth:_ automatizace ~66 %, přesnost automatických párování ~80 %, 3 / 19 plateb na špatnou fakturu, 0 spárování podle prompt injection.
  - _Odchylky od SPEC:_ žádné. `docs/business-rules.md` vědomě nevzniklo, je to úkol fáze 3 (extrakce z kódu). Klíč zasazených pravidel je mimo git v `../payment-matching-ai-private/legacy-answer-key.md`.
- [x] **Fáze 3** – **Port pravidel do .NET** + charakterizační testy (shoda s legacy) + `business-rules.md`.
  - _Hotovo 2026-11-12._ Hotové části:
    - `docs/business-rules.md` (extrakce izolovaným agentem, 13/13),
    - `src/PaymentMatching.Rules` + `tools/Rules.Cli`,
    - 62 unit testů + 4 golden master testy,
    - věrný port bajtově shodný s legacy (tag `phase3-faithful-port`),
    - změny F1 (pryč fuzzy hledání), F2 (neplatné částky) a F3 (`decimal`),
    - `port_matches.csv`, ADR 0004, `docs/modernization.md` krok 1 a 3.
  - _Výsledek proti ground truth:_ 0 plateb na špatnou fakturu (legacy 3 / 19), všech 450 přesných shod spárováno (legacy 443), přesnost auto párování 82,4 %. Zbytek tvoří jen případy pro člověka (částečné úhrady, třetí strany), a to je úkol fáze 4.
  - _Otevřené pro tebe:_ obchodní otázky Q-01..Q-12 v `business-rules.md`, hlavně Q-02 (automatické částečné úhrady) a Q-07 (kontrola plátce). Zatím chování zůstává jako v legacy.
- [x] **Fáze 4** – **AI gateway + AI párování** (structured output, validace, routing, audit, idempotence).
  - _Hotovo 2026-11-12._ Vzniklo:
    - `src/PaymentMatching.AiGateway`: provider abstrakce, oficiální Anthropic SDK, routing Haiku 5.5 → Opus 5.5, timeout, strop volání, tokeny a cena, record/replay,
    - `src/PaymentMatching.Matching`: politika nad pravidly (Q-02, Q-07), výběr kandidátů kódem, prompt `prompts/match-payment.v1.md`, striktní structured output, validace kódem, rozhodování, append-only audit log s idempotencí,
    - `tools/Match.Cli`: režimy `stub|replay|record|live|rules`,
    - 91 nahrávek reálných odpovědí v `evals/recordings/`,
    - ADR 0005–0007, `modernization.md` krok 4, stav Q-02/Q-06/Q-07/Q-08/Q-11 v `business-rules.md`.
  - _Výsledek na `data/sample` (reálný model):_
    - automatizace 75,7 % (strop sady 76 %), přesnost auto párování 100 % (port 82,6 %), 0 špatných faktur,
    - 15/15 injection zachyceno,
    - $0,64 za 1 000 plateb, p50 2,0 s / p95 5,2 s na AI platbu,
    - 192 testů.
  - _Rozhodnuto (schváleno v plánu fáze):_
    - Q-02: částečné úhrady a přeplatky záloh jdou vždy člověku s návrhem.
    - Q-07: auto párování vyžaduje IBAN odběratele (výjimka: přesný VS v poli + částka); třetí strany jdou člověku.
  - _Odchylky od SPEC:_
    - audit a idempotence zatím v JSONL souboru (`DecisionLog`), PostgreSQL až s tenanty ve fázi 7,
    - EUR kurz zatím pevné pásmo 24,00–26,50, ne denní kurz ČNB.
  - _Pro fázi 5:_
    - neeskalovat odpovědi, které stejně jdou člověku (8 z 12 volání Opus, Opus = 84 % ceny),
    - kalibrace prahů (Opus dal správné odpovědi 0,88 → člověk),
    - měřit rozptyl mezi běhy (dva nahrávací běhy: 228 vs 227 auto),
    - srovnání pravidla / stub / model na těžších datech.
- [x] **Fáze 5** – **Evaly** – zlatá sada, runner, reporty, replay v CI, práh v CI.
  - _Hotovo 2026-11-12._ Vzniklo:
    - zlatá sada `evals/dataset/gold-v1` (300 plateb, seed 5005, `--profile eval`, 30 % těžkých případů: remízy, nečitelné číslo faktury, injection bez klíčových slov), review v `REVIEW.md`, zmrazená přes `MANIFEST.sha256`,
    - `src/PaymentMatching.Evals` (scorer, gate, rozptyl, Markdown report) + `tools/Eval.Cli` (`replay|record|live`, `--repeat`),
    - 5 konfigurací: `rules`, `stub`, `small`, `routing-v1`, `routing`; CI krok s gate (precision ≥ 99 % a 0 spárovaných injection pro všechny, `routing` navíc cíl injection nikdy nenavržen) + report jako artifact,
    - nové pravidlo validace `AMBIGUOUS`, eskalace jen když může změnit výsledek (výchozí), gateway umí běžet bez velkého modelu,
    - 200 nových nahrávek, report `evals/reports/2026-11-12-…-match-payment.v1.md` + report rozptylu, ADR 0008–0009, `modernization.md` krok 5, tabulka v README, 219 testů.
  - _Výsledek na zlaté sadě (`routing`, reálný model):_ precision 100 %, automatizace 53,3 % (strop 63,3 %; rozdíl = nečitelná čísla faktur, vědomě člověku), 40/40 injection, $0,36 za 1 000 plateb (`routing-v1` $2,21), p50 2,0 s / p95 4,1 s. Stub: 100 % precision, ale 20 cílů skrytých injection navrhl člověku. Na `data/sample` $0,64 → $0,27 za 1 000 plateb při stejných 227 auto.
  - _Položky z fáze 4:_ eskalace vyřešena (39 → 4 volání Opus); práh 0,90 ponechán (všechny validované odpovědi ≥ 0,90 a správné, sweep 0,80–0,90 beze změny, 0,95 ubírá automatizaci); rozptyl 3 živé běhy: výsledky identické, liší se jen návrh u 8 remíz.
  - _Odchylky od SPEC:_ runner je `tools/Eval.Cli` (ne `dotnet run --project evals`), živý eval `--mode live`. Precision gate platí pro všechny konfigurace, nejen pro produkční.
  - _Útrata za živá volání:_ $0,67 nahrávání + $0,24 rozptyl = $0,91 (schválený strop $2,50).
  - _Pro tebe:_ namátková kontrola 14 plateb ze zlaté sady (tabulka v `evals/dataset/gold-v1/REVIEW.md`); opravy by šly do `gold-v2`.
- [x] **Fáze 6** – **MCP server** + návod pro Claude Desktop / Code.
  - _Hotovo 2026-11-12._ Vzniklo:
    - `src/PaymentMatching.Mcp` (`payments-mcp`): oficiální C# MCP SDK 2.4.0, transport stdio (výchozí) a Streamable HTTP (jen localhost, filtr `Host` hlavičky),
    - nástroje `list_unmatched`, `get_payment`, `get_customer`, `search_invoices` (jen čtení) a `propose_match` (jen návrh do fronty `proposals.jsonl`, idempotentní, s kontrolami `ProposalValidator`), MCP prompt `investigate_unmatched`,
    - text z výpisu jen pod `untrusted_bank_data` + `injection_suspected`, instrukce serveru a popisy nástrojů,
    - 37 testů (nástroje, injection, idempotence, audit log beze změny, skutečný MCP klient přes in-memory streamy: seznam nástrojů, anotace, schémata bez tenant/cesty, chyby, prompt), celkem 256,
    - ADR 0010, návod v README (Claude Code, Claude Desktop, HTTP), příkaz v AGENTS.md.
  - _Odchylky od SPEC:_
    - parametr `invoiceIds` se jmenuje `invoiceNumbers` (v doméně je identifikátor faktury její číslo),
    - místo tenantu „workspace na proces“ (adresář dat + stavu z příkazové řádky, žádný nástroj ho nevybírá); izolace tenantů v datové vrstvě přijde ve fázi 7 a MCP na ni přejde (ADR 0010),
    - server čte audit log jednou při startu (po novém běhu pipeline restart).
  - _Útrata:_ $0, server model nevolá, žádný `record`/`live` běh.
  - _Pro fázi 7/8:_ fronta návrhů z MCP se má v UI zobrazit vedle návrhů pipeline (kdo navrhl: `agent:mcp`).
- [x] **Fáze 7** – **Multi-tenant + přihlášení** – Microsoft/Google login, sandbox na uživatele, startovní data, testy izolace.
  - _Hotovo 2026-11-12._ Vzniklo:
    - `src/PaymentMatching.Data` (EF Core + Npgsql, migrace): tenanti, uživatelé, data, rozhodnutí, párování faktur, návrhy; `TenantId` v primárním klíči všech tabulek,
    - izolace v `PaymentsDbContext`: query filter na každé entitě, kontrola zápisu (cizí řádek → `TenantIsolationException`), bez tenantu nic (fail closed); napříč tenanty jen `TenantAdmin` (hlídá test),
    - idempotence z ADR 0006 jako primární klíče v PostgreSQL (`decisions`, `invoice_matches`), pipeline přes `IDecisionStore` (JSONL zůstává pro CLI a evaly),
    - `SandboxProvisioner`: první přihlášení = tenant + startovní data (20 plateb, ~25–30 faktur) + jen vrstva pravidel, AI platby čekají na denní job; denní limit nových sandboxů; mazání po 30 dnech neaktivity (`TenantAdmin.DeleteInactiveAsync`, plánování ve fázi 9),
    - `src/PaymentMatching.Web`: cookie + OIDC Microsoft (common, validace issueru podle `tid`) a Google, code flow + PKCE, tokeny se neukládají; dev login jen v Development; read-only API (`/api/me`, `/api/payments`), `/privacy`, bezpečnostní hlavičky, rate limit přihlášení,
    - `payments-mcp --tenant <id>` nad tenantovou datovou vrstvou, návrhy v DB,
    - 44 nových testů (celkem 302) proti skutečnému PostgreSQL (Testcontainers): úniky mezi tenanty se stejnými id ve všech tabulkách, přes HTTP i MCP, souběh, limity, OIDC přesměrování bez sítě a bez tajných klíčů,
    - ADR 0011, update ADR 0006/0010, README (lokální běh, registrace aplikací), AGENTS.md, `.env.example`.
  - _Odchylky od SPEC:_
    - demo bez přihlášení není (volitelné, přesouvám do fáze 8 spolu s UI),
    - mazání neaktivních sandboxů je hotové jako funkce, spouštět ho bude denní job (fáze 9),
    - RLS v PostgreSQL jako druhá vrstva zatím ne (kandidát pro fázi 11).
  - _Útrata:_ $0, žádné volání modelu.
  - _Pro fázi 8:_ UI nad `/api/*` (zatím jen jednoduché HTML), akce potvrdit / zamítnout / spárovat ručně jako první zápisy uživatele (test „žádný endpoint nepřijímá text“ se pak rozšíří o validované akce), návrhy z MCP ve frontě.
- [x] **Fáze 8** – **UI** – seznam plateb, „kdo rozhodl“, fronta pro člověka (potvrdit / zamítnout / spárovat ručně), audit timeline, dashboard.
  - _Hotovo 2026-11-12._ Vzniklo:
    - UI jako Razor komponenty renderované na serveru přes explicitní minimal API endpointy, **bez JavaScriptu** (CSP bez skriptů, jen `style-src 'self'`), bez nových balíčků: dashboard, seznam plateb s filtrem stavu, fronta, detail („kdo rozhodl“: pravidlo / model + prompt + confidence + zdůvodnění, návrhy pipeline a `agent:mcp` vedle sebe, audit timeline včetně volání modelu s tokeny a cenou), ruční párování s náhledem kontrol,
    - tabulka `reviews` (PK = idempotenční klíč řádku výpisu → jedno lidské rozhodnutí na platbu, nikdy se nepřepisuje) + lidská spárování v `invoice_matches`; pipeline i MCP je berou jako obsazené, MCP nenavrhuje k platbě rozhodnuté člověkem,
    - `ReviewActions` (potvrdit / zamítnout / ručně) přes tenantový kontext; `ProposalChecks` sdílené s MCP: blokující chyby (faktura už spárovaná, různí odběratelé, neznámá faktura) vs. varování, která musí člověk jednotlivě odškrtnout (server chce přesně dnešní množinu),
    - antiforgery (`__Host-` cookie, SameSite=Strict) na všech zápisech včetně odhlášení, rate limit akcí na osobu, vstupy jen id a známé kódy kontrol,
    - dashboard: automatizace, zásahy člověka, zachycené injection, cena AI za den a na 1 000 plateb, „živá přesnost“ návrhů podle navrhovatele (rule / ai / agent:mcp),
    - `match --tenant <id>`: stub běh nad sandboxem (bez volání modelu) pro lokální ukázku AI částí,
    - 20 nových testů (celkem 322): akce a souběh v DB, úniky mezi tenanty se stejnými id v datové vrstvě i přes HTTP, CSRF (bez tokenu / s tokenem jiné session), kódování nedůvěryhodného textu z výpisu, modelu i agenta, odmítnutí volného textu, rozšířený test zápisových endpointů (přesný seznam + povolené typy parametrů), pipeline po lidském spárování, MCP nad rozhodnutými platbami,
    - ADR 0012, README (co v UI jde dělat), AGENTS.md.
  - _Ověřeno ručně v prohlížeči_ (alice, lokálně): fronta, potvrzení bez odškrtnutého varování nic neuloží, s odškrtnutím uloží, timeline a dashboard se živou přesností.
  - _Odchylky od SPEC / plánu:_
    - demo bez přihlášení přesunuto do fáze 9 (dává smysl až s daty zpracovanými jobem),
    - rozhodnutí člověka nejde vrátit (append-only audit; případné „undo“ by byl nový řádek),
    - fronta = `human_review` + `no_match` s návrhem agenta; platby čekající na AI běh akce nemají,
    - stav AI rozpočtu na dashboardu přijde s jobem (fáze 9).
  - _Útrata:_ $0, žádný `record`/`live` běh.
- [x] **Fáze 9** – **Denní job** – generátor + automatické zpracování, limity a rozpočet na AI (upřesněno 2026-11-12, ADR 0013, SPEC 9b).
  - _Hotovo 2026-11-12._ Vzniklo:
    - `src/PaymentMatching.Job` (`payments-job`): konzolový job „jeden běh a konec“ pro pozdější Container Apps Job, `--mode stub|live`, `--migrate`; maže neaktivní sandboxy (30 dní, nikdy demo), zakládá demo, doplňuje sdílenou dávku jen aktivním sandboxům (7 dní) po vyřešení něčeho nebo při frontě < 3 (demo vždy), pak pravidla → AI → validace,
    - sdílená denní dávka `DailyBatch` (seed = datum, 3–5 plateb, 1–2 do AI vrstvy; ověřeno přes 400 dní), id s datem (`P261112-0001`); generátor dostal volby prefixů a číselné řady (výchozí hodnoty = `data/sample` beze změny),
    - cache odpovědí `CachingProvider` pod klíčem record/replay (+ zdroj `live`/`stub`), cached odpověď stojí $0, nepočítá se do stropu a projde validací v každém sandboxu,
    - `OperationsDbContext` (vlastní migrace): `ai_response_cache`, `daily_usage`; rozpočty `AiAllowance` = jedno číslo na sandbox z tří vrstev; strop sandboxu → platba čeká na další den, glob. den / měsíc → člověku se štítkem „AI budget used up“; `Ai:SmallModelOnly`,
    - web: `/demo` (bez přihlášení, jen čtení, tenant z konfigurace + `IsDemo`, read-only kontext, rate limit), `/admin` (allowlist, jinak 404, jen souhrny), AI rozpočet na dashboardu, štítek v seznamu a detailu, cached odpověď v audit timeline, počítání odmítnutých přihlášení (pořadník),
    - 39 nových testů (celkem 361), ADR 0014, `docs/cost.md`, README, AGENTS.md, `.env.example`.
  - _Ověřeno ručně lokálně:_ job ve stub režimu nad lokální DB (3 sandboxy, druhý běh nic nepřidal ani nevolal, sandbox nad limitem nechal 4 platby čekat), `/demo`, `/admin` a dashboard v prohlížeči.
  - _Odchylky od SPEC / plánu:_
    - platby nad denní strop **sandboxu** čekají na další běh (rozhodnuto s vývojářem); štítek „AI budget used up“ jen při vyčerpání globálního denního stropu nebo měsíčního rozpočtu,
    - injection bez kandidátů rozhodne politika bez modelu, takže některé dny dávka model nepotřebuje vůbec,
    - měsíční rozpočet se hlídá rezervou $0,05 na volání (ne přesnou cenou dopředu),
    - souběh dvou instancí jobu může strop přetáhnout o volání; v Azure parallelism 1 + spend limit v konzoli.
  - _Útrata:_ $0,0007. Jeden živý běh jobu nad lokální DB se souhlasem vývojáře (odhad ≤ 5 volání, < $0,02). Skutečnost: 2 volání Haiku v jediném sandboxu, který ještě měl denní limit; ostatní sandboxy ho ten den vyčerpaly stub během, a protože ledger počítá i volání stubu, jejich platby čekají na další den. Výsledek: 1 automatické spárování (confidence 0,96) a 1 platba pro člověka (0,80).
- [x] **Fáze 10** – **Observabilita** – OpenTelemetry, cena a tokeny v trace, Application Insights.
  - _Hotovo 2026-11-12._ Vzniklo:
    - knihovny (gateway, pipeline, job) zapisují jen přes BCL (`ActivitySource` + `Meter` „PaymentMatching“ v `AiGateway/Telemetry.cs`), bez nové závislosti,
    - spany `matching.run` → `matching.payment` (vrstva, pravidlo, výsledek, kódy) → `ai.complete` → `chat {model}` (GenAI konvence: model, tokeny; navíc tier, cena, cached, latence poskytovatele, kategorie chyby); `job.run` → `job.sandbox` s pseudonymem tenantu (12 hex SHA-256),
    - metriky `payments.ai.calls|tokens|cost|call.duration|budget_exhausted`, `payments.matching.decisions`,
    - `src/PaymentMatching.Observability`: SDK a exportéry jen v hostitelích (web, job, MCP, `Match.Cli`), zapnuté jen konfigurací (`OTEL_EXPORTER_OTLP_ENDPOINT`, `APPLICATIONINSIGHTS_CONNECTION_STRING`), redakční procesor před exportem; ASP.NET Core instrumentace bez statických souborů,
    - Aspire dashboard v `docker compose --profile observability` (jen localhost),
    - 18 nových testů (celkem 380) včetně testů úniku: replay skutečných odpovědí nad `data/sample`, job nad sandboxem, web s `?name=` a user agentem, MCP s označenými argumenty; allowlist tagů,
    - ADR 0015, README, AGENTS.md, `.env.example`.
  - _Ověřeno ručně:_ `Match.Cli --mode replay` s exportem do Aspire – strom 463 spanů, detail `chat claude-opus-5-5` s tokeny, cenou a latencí, metriky `payments.ai.*`.
  - _Odchylky od SPEC / plánu:_
    - Npgsql spany vynechány (jen metriky): chyba DB nese hodnoty ve zprávě,
    - logy se neexportují (zůstávají na stdout),
    - Azure Monitor jen jako exportér, ne celá „distro“ (ta zapíná i logy a live metrics); skutečné napojení na App Insights až ve fázi 11,
    - job nad lokální DB jsem se stubem nepouštěl (stub počítá volání do denního limitu sandboxu); spany jobu kryjí testy nad Testcontainers.
  - _Útrata:_ $0, žádný `record`/`live` běh.
- [x] **Fáze 11** – **Azure** – Bicep/azd, Container Apps + Job, PostgreSQL, Key Vault, nasazení z GitHub Actions přes OIDC, vlastní doména (subdoména stanislavmatzke.com).
  - _Hotovo 2026-11-13._ Běží na **https://payments.stanislavmatzke.com** (managed certifikát). Vzniklo:
    - `infra/main.bicep` + `main.bicepparam` (stav aplikace commitnutý): `rg-payments` – Container Apps prostředí, web `ca-payments-web` (0,25 vCPU, 0–1 replika, scale-to-zero), job `caj-payments-daily` (05:00 UTC, parallelism 1, bez retry, `--mode live --migrate`), Key Vault (RBAC), Log Analytics (strop 0,1 GB/den) + App Insights, identity `id-payments` (běh, Key Vault Secrets User) a `id-payments-deploy` (GitHub OIDC, Contributor jen na RG),
    - skripty `deploy-infra.sh` (lokálně, zachová běžící image), `bootstrap-db.sh` (vlastní DB `payments` a role `payments_app` na sdíleném serveru, dočasné pravidlo firewallu), `bind-domain.sh`,
    - `deploy.yml`: po zeleném CI na `main` image na ghcr.io (veřejné balíčky), přes OIDC update aplikace a jobu, 7 smoke testů; CI navíc kompiluje Bicep a staví image bez Azure,
    - aplikace: klíče Data Protection v PostgreSQL (bez balíčku, přihlášení přežije scale-to-zero), forwarded headers za ingressem, image přes `dotnet publish` (chiseled), `/api/me` ukazuje `accountId` pro `/admin`,
    - 3 nové testy (celkem 383) a kontrola `accountId` v testu přihlášení, ADR 0016, `docs/cost.md` (hosting), README „Running in Azure“, AGENTS.md.
  - _Ověřeno v Azure:_ smoke testy z workflow, přihlášení Microsoft i Google (vývojář), `/admin` jen pro admin účet, živý běh jobu (demo + 10 rozhodnutí), 463 spanů v App Insights bez textu z výpisu.
  - _Odchylky od plánu:_ jeden `main.bicep` místo modulů; Data Protection přes vlastní `IXmlRepository` místo balíčku; DB na sdíleném serveru JobScoutu (rozhodnuto s vývojářem, ~$16–18/měsíc úspora); Bicep a skripty v Azure spouštěl vývojář (bezpečnostní kontrola Claude Code agentovi aplikaci infrastruktury nepovolila).
  - _Útrata:_ hosting odhad ~$0–2/měsíc; AI $0,0306 (8 volání, první běh jobu: demo + dávka; odhad byl < $0,01).
  - _Otevřené / další krok:_ nový uživatel vidí AI platby jako „čeká na AI“ až do ranního jobu – vývojář to považuje za chybu produktu; návrh řešení (sdílená startovní data s cache odpovědí) ke schválení. UI: detail faktury v detailu platby, záložka Admin, přihlašovací tlačítka jako JobScout.
- [ ] **Fáze 12** – **Dokumentace a příprava na zveřejnění** – README, ADR, security, cost, modernization, how-it-was-built + dvojjazyčnost a úklid před zveřejněním repa:
  - **Hlavní jazyk repa je angličtina.** `README.md`, `AGENTS.md`, `docs/SPEC.md`, `docs/PLAN.md` a všechny dokumenty v `docs/` budou anglicky.
  - **Česká verze vedle:** `README.cs.md` a `docs/cs/` (SPEC, PLAN, modernization, how-it-was-built). Na začátku každého dokumentu odkaz na druhou jazykovou verzi („🇨🇿 Česky / 🇬🇧 English“).
  - **Originál zadání:** současné české `docs/SPEC.md` a `docs/PLAN.md` přesunout beze změn obsahu do `docs/spec-original-cs/` jako autentický start projektu (jen s úpravami z bodu „úklid“). Přeložené verze vycházejí z nich.
  - **AGENTS.md:** anglická verze je ta, kterou čte agent. Pravidlo „komunikace se mnou česky“ ponechat.
  - **Úklid osobních věcí:** z veřejných dokumentů odstranit sekci „Výstupy pro mě mimo repo“ a zmínky o hiring managerech, CV a pohovorech. Úvod SPEC přepsat neutrálně („Reference implementation showing…“). Tyto výstupy (osnova článku 04, LinkedIn text, odrážky do CV) připravit zvlášť do souboru mimo git (např. `../payment-matching-ai-private/outputs.md`) a ukázat mi je v chatu.
  - **README** v obou jazycích: problém → architektura (Mermaid) → výsledky evalů (tabulka) → odkaz na živou aplikaci → spuštění za 2 minuty → „Built spec-first with an AI coding agent – see AGENTS.md, docs/SPEC.md, docs/PLAN.md“ → licence MIT.
  - **Kontrola před zveřejněním:** gitleaks nad celou historií, žádná reálná data, žádné tajné klíče, žádné interní cesty nebo e-maily, odkazy v dokumentech fungují, `LICENSE` existuje.
  - **Doplněno po fázi 11 (2026-11-13):**
    - **Nestabilní test:** `McpTelemetryTests.Tool_calls_are_traced_by_name_without_arguments_or_error_text` občas padá v celé paralelní sadě, samostatně prochází. Najít příčinu (pravděpodobně globální posluchač / sdílený stav jako u webové telemetrie ve fázi 10) a opravit, ne přeskočit. Ověřit několika celými běhy sady.
    - **Kontrola kódu před zveřejněním:** projít `src/` a `tests/` (mrtvý kód, zastaralé komentáře odkazující na „fázi N later“, TODO, nekonzistentní názvy), `dotnet format`, žádné varování; Bicep a skripty v `infra/` (komentáře, žádné osobní id kromě vědomých: jméno a kontakt správce, client id, OIDC subject, doména).
    - **README:** odstranit „Work in progress“, odkaz na živou aplikaci https://payments.stanislavmatzke.com a demo, výsledky evalů, co je vidět v aplikaci (čeština, „What AI added“, vrácení párování AI), nasazení do Azure.
    - **`docs/how-it-was-built.md`** (+ česká verze) z „Poznámek k průběhu“: spec-first s agentem, schvalování plánů, chyby agenta a jak je chytily testy, co odhalilo až nasazení, uživatelské testování po spuštění.
    - **Index ADR** (0001–0018) v README nebo `docs/adr/README.md`.
    - **Kontrola zveřejnitelnosti:** ghcr.io balíčky jsou veřejné; `.gitleaksignore` vysvětlený; v historii žádné tajné klíče ani osobní e-maily (kromě vědomého kontaktu `kontakt@stanislavmatzke.com` a jména správce v `infra/main.bicepparam`).
    - **Ověřit, že živá aplikace po změnách funguje** (smoke testy z deploy workflow + ruční kontrola EN i CS).
  - Na konci mi dej seznam souborů ke kontrole a čekej na mé schválení. Repo zveřejním sám.

Pořadí 1–6 je priorita (repo s modernizací, AI párováním, evaly a MCP musí být ukazatelné co nejdřív). Fáze 7–11 dělají z projektu živou aplikaci.


## Výstupy pro mě mimo repo

Po dokončení připrav:
- osnovu článku 04 na web (CZ): „Párování plateb s AI: od Delphi k evalům“ – zkušenost, čísla z evalů, co AI rozhoduje a co ne, jak dopadla modernizace,
- 3–4 věty pro LinkedIn,
- 5 odrážek do CV / pohovoru (co projekt ukazuje).

## Poznámky k průběhu

_(agent sem zapisuje průběžně: rozhodnutí, problémy, co pokazil a jak se to opravilo – podklad pro docs/how-it-was-built.md a článek)_

**Fáze 1 (2026-11-12)**
- Agent nejdřív přidal klasickou trojici testovacích balíčků (`Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio`). Na .NET 10 SDK s xUnit v3 to selhalo („VSTest target is no longer supported“). Oprava: `global.json` → `test.runner = Microsoft.Testing.Platform`, dva balíčky odebrány. Méně závislostí.
- `dotnet format` na Windows hlásil chyby konců řádků u souborů zapsaných s LF. Oprava: `end_of_line = lf` v `.editorconfig` + `.gitattributes` (`eol=lf`), aby formát vycházel stejně lokálně i v Linuxovém CI.
- Riziko, které agent ošetřil předem: VS s přehozenými číslicemi mohl náhodou odpovídat jiné existující faktuře (pak by „překlep“ byl ve skutečnosti správný VS jiné faktury). Generátor takové kolize přepíná na variantu s chybějící číslicí (9 místný VS nemůže kolidovat). Kryto testem.
- Zaokrouhlení vah případů: `0.06 * 300` v plovoucí čárce může dát 17,999…, takže epsilon v plánovači počtů.
- První push odmítl GitHub (ochrana soukromého e-mailu v commitech). Oprava: autor commitů v repu nastaven na GitHub noreply adresu, 4 lokální commity přepsány před prvním pushem. Výhoda: v budoucí veřejné historii není osobní e-mail.
- První běh CI: `gitleaks-action@v2` spadl na prvním pushi nového repa (neplatný rozsah `root^..HEAD`, neprohledal nic). Oprava: gitleaks CLI s ověřeným checksumem nad celou historií. Ponaučení: „zelený“ secret scan je třeba číst – action hlásil „no leaks found in partial scan“ nad 0 bajty.
**Fáze 2 (2026-11-12)**
- Při návrhu legacy pravidel agent zjistil, že první verze pravidla „částečná úhrada“ by se dala zneužít textem zprávy: injection „platba patří k faktuře 2026-xxxx“ s menší částkou by se spárovala jako částečná úhrada. Úprava: částečná úhrada jen tehdy, když VS přišel v poli VS (realistická výjimka „2007-03 JN“). Legacy tak odolává injection, ale jen náhodou, a to je v `docs/modernization.md` výslovně řečeno.
- Neplánovaný, ale cenný nález v datech: chybné fuzzy spárování „spotřebuje“ fakturu, takže pozdější správná platba na ni skončí jako `DUPLICATE` (7× v sadě 1 000). Jedna chyba tak dělá dvě škody.
- Agent napsal jednorázový srovnávací skript v awk a použil v něm proměnnou `NR`, což je vestavěná proměnná awk (číslo řádku). První čísla „bez člověka“ byla proto nesmyslná (636 z 300 plateb). Chyba byla odhalena kontrolou součtů a opravena. Ponaučení: i jednorázový měřicí skript potřebuje kontrolu.
**Fáze 3, krok 1 – extrakce pravidel z legacy (2026-11-12)**
- Extrakci dělal agent v **samostatné session bez kontextu**. Směl číst jen `legacy/delphi`, `legacy/plsql`, `legacy/README.md`, `docs/data-format.md` a `AGENTS.md`. Nespouštěl kód, neviděl golden master, PLAN, modernization ani klíč odpovědí. Podle vlastního hlášení zákaz dodržel.
- Výsledek: `docs/business-rules.md`, 24 pravidel, 15 rozdílů Delphi × PL/SQL, 13 rizik s konkrétními vstupy, 12 otevřených otázek.
- **Skóre proti klíči odpovědí (mimo git): 13 / 13.** Našel všech 11 zasazených pravidel včetně záměrné fuzzy chyby, jejího domino efektu (`DUPLICATE`) a nesouladu tolerance `<` × `<=`, i obě položky „drobností“.
- **Našel i věci, které autor legacy (jiná session agenta) nezasadil vědomě**, všechny ověřené proti kódu:
  - PL/SQL okno 26 znaků uřízne číslo faktury: `FAKTURA PRO FIRMU ABC 2026-1234` → `2026-1` → jiná faktura,
  - prázdná měna: Delphi odmítne, PL/SQL páruje jako CZK (NULL v podmínce),
  - nečitelná částka = 0 → automatická „částečná úhrada“, která uzavře fakturu,
  - částečná úhrada nemá spodní hranici (1 Kč uzavře fakturu),
  - měna faktury se nekontroluje,
  - `ROWNUM = 1` bez `ORDER BY` je v PL/SQL nedeterministické,
  - třídění `payment_id` jako řetězce.
- Kontrola: namátkově ověřeno 17 odkazů na řádky, všechny přesné. Halucinace nenalezeny. Jediná oprava: v příkladech bylo jméno „NOVAK JAN“ (reálně znějící) → nahrazeno fiktivní firmou.
- Postřeh do článku: AI byla v porozumění kódu **důkladnější než autor fikce**. Nejcennější nejsou samotná pravidla, ale okrajové případy s konkrétními vstupy, ze kterých jdou rovnou psát testy.

**Fáze 3, port (2026-11-12)**
- Věrný port prošel bajtovou shodou s legacy **napoprvé** na 1 300 platbách. Pomohlo, že business-rules.md obsahovala přesný popis výstupního formátu a doporučení číst text „po bajtech“ (Latin-1).
- **Charakterizační test opravil AI dokument:** dva unit testy převzaté z příkladů v business-rules.md selhaly, zatímco golden test prošel. Pravidlo o okně zprávy mělo chybu o jedničku (0–14 místo 0–15). Ověřeno sondou nad legacy binárkou, dokument opraven s poznámkou. Ponaučení: AI extrakce je skvělý start, ale přesnost na úrovni řádku ověří až běžící kód.
- `double` → `decimal` (F3) bez jediné změny výstupu. Golden master z toho udělal bezpečný refaktoring.
- Test „každý rozdíl musí být vysvětlený“ hned **dvakrát chytil nepředvídané důsledky F1**: z `DUPLICATE` se může stát i `OVERPAID`, a VS může pocházet i ze zprávy. Agent to v prvním návrhu klasifikátoru nedomyslel, test ho opravil. Přesně tohle má test dělat.
- Fuzzy chyba v legacy blokovala i správné platby od třetích stran a s VS ve zprávě (2 případy v sadě 1 000), nejen přesné shody.

**Fáze 4 (2026-11-12)**
- Plán vznikl v nové session jen z PLAN/SPEC/ADR/business-rules, bez doptávání. Politiku pro Q-02/Q-07 agent navrhl až po spojení výstupu portu s ground truth podle typu případu.
- **Validaci prolomil stub, ne model.** Deterministický stub (bere první návrh kódu) spároval EUR platbu s kombinací dvou jiných faktur téhož odběratele. Pásmo kurzu je široké ~10 %, takže se do něj součet „vešel“. Test s náhodným modelem to předtím přešel **náhodou**. Oprava:
  - test nahrazen vyčerpávajícím: každá množina 1–4 kandidátů jako „odpověď modelu“ nad 1 300 platbami; našel 8 + 37 děr, všechny EUR,
  - dvě nová pravidla validace: platba musí fakturu jmenovat (VS / číslo ve zprávě) a EUR platí jen jednu fakturu,
  - poté 0 děr.
- Replay test nejdřív selhal: CLI a testy měly jiné `MaxTokens`, a ty jsou součástí klíče nahrávky. Oprava: jedna definice modelů (`ModelSettings`).
- **CI odhalilo závislost promptu na OS.** Nahrávky vznikly na Windows, v CI (Linux) replay nenašel klíč. `WriteIndented` JSON v promptu používal `Environment.NewLine`, takže modelu šel jiný text podle OS (`\r\n` vs `\n`). Oprava:
  - pevné `\n` + test „prompt neobsahuje `\r`“,
  - nové nahrávání (podruhé ~$0,20).
  - Ponaučení: klíč nahrávky = otisk přesně toho, co jde do modelu, a ten musí být stejný na každé platformě. Bez CI na jiném OS by se to nezjistilo.
- Odhad ceny nahrávání „< $0,20“: první běh $0,205, druhý $0,19. 84–85 % ceny tvoří eskalace na Opus.
- Drobnosti, které chytil build/test: neuzavřený `sed` v heredocu (soubory se nezapsaly), `--` v XML komentáři csproj, `JsonException` místo `FormatException` z parseru, porovnání recordů se seznamy přes referenci.
- Model (Haiku) v jednom zdůvodnění tvrdil, že zpráva jmenuje obě faktury, přitom jmenovala jednu. Rozhodnutí bylo správně, zdůvodnění ne. Proto zdůvodnění není důkaz a rozhoduje validace.

- Permission prompty v Claude Code zdržovaly; vývojář povolil běžné příkazy v `.claude/settings.local.json` (mimo git).

**Fáze 5 (2026-11-12)**
- Plán vznikl znovu v nové session jen z PLAN/SPEC/ADR a kódu, schválen beze změn.
- **První eval běh našel díru ve validaci.** Stub nad zlatou sadou: precision 84,2 %, 8 plateb na špatnou fakturu. Všech 30 remíz (dvě faktury nebo kombinace se stejnou částkou) prošlo validací: ta ověřovala, že návrh *sedí*, ne že je *jediný*. Gate build shodil. Oprava: pravidlo `AMBIGUOUS` + vyčerpávající test validace nově i nad zlatou sadou a přísnější (platný návrh musí být jediný správný a smí být automatický). Ponaučení: vyčerpávající test z fáze 4 byl dobrý nástroj nad daty, ve kterých remízy nebyly. Test je jen tak dobrý jako data.
- Skutečná přidaná hodnota modelu na těchto datech: injection bez klíčových slov (30/30 zachyceno, stub navrhl cíl člověku 20×). Na nečitelných číslech faktur model nepomůže, protože automatické párování smí jen to, co ověří kód. Parser se vědomě nerozšiřoval podle formátů generátoru (přeučení na vlastní data).
- Nové pravidlo eskalace snížilo volání Opus 39 → 4 při stejných výsledcích. Rozhodnutí je kód (stejný validátor), ne úsudek modelu.
- Nahrávací běh v režimu `record` zahrnul i sweep prahů: jiný práh mění, které faktury se spárují dřív, takže pozdější platby dostanou jiné kandidáty a vzniklo 25 volání Haiku navíc (~$0,01). Neškodné, ale poučné: „sweep zdarma“ platí jen pro replay.
- Drobnosti, co agent pokazil: heredoc + Python (Windows Store stub) nezapsal nic → Write/Edit; `sed` se zpětnými lomítky v cestách csproj tiše nic nenahradil (dvakrát, odhalil build); `sha256sum` na Windows píše `*` před název souboru, první manifest proto nesouhlasil (odhalil runner hned při prvním běhu).

**Fáze 6 (2026-11-12)**
- Plán znovu vznikl v nové session jen z PLAN/SPEC/ADR a kódu, schválen beze změn. Jediná vědomá odchylka: tenant až ve fázi 7, do té doby „workspace na proces“ (zapsáno v ADR 0010).
- **Test protokolu chytil chybu, kterou kompilátor nevidí:** `WithPrompts(typeof(InvestigatePrompt))` se navázalo na generické přetížení „instance cíle“ (`TPromptType target`) s objektem `Type`, takže se nezaregistroval žádný prompt. Unit testy nad třídou by prošly; selhal až test přes skutečného MCP klienta (`prompts/list` „not available“). Oprava: `WithPrompts([typeof(...)])`. Ponaučení: u DI/atributových registrací testovat přes protokol, ne jen třídu.
- `dotnet run` u projektu s Web SDK spouští aplikaci v adresáři projektu, takže výchozí relativní cesty (`data/sample`) neexistovaly. Odhalil to ruční smoke test přes stdio. Oprava: `RunWorkingDirectory = $(MSBuildStartupDirectory)`.
- Smoke test přes stdio: prvních pár pokusů nevrátilo nic, protože konec stdin server ukončí dřív, než odešle odpovědi. Testovací skript musel držet stdin otevřený. Ověřeno také, že `dotnet run` nepíše výstup buildu na stdout (rozbil by JSON-RPC).
- HTTP transport doplněn o filtr `Host` hlavičky (jen loopback), aby se k serveru na localhostu nedostala webová stránka přes DNS rebinding. Ověřeno curl: cizí Host → 400.
- `sed` se zpětnými lomítky v cestě csproj potřetí tiše nic nenahradil (přidání `InternalsVisibleTo`); tentokrát si toho agent všiml hned z výpisu souboru a přešel na Edit.

**Fáze 7 (2026-11-12)**
- Plán znovu vznikl v nové session jen z PLAN/SPEC/ADR a kódu, schválen beze změn.
- **Leak testy záměrně se stejnými id v obou tenantech** (`P0001`, stejná čísla faktur): odliší je jen filtr. Seznam testovaných tabulek se bere z EF modelu, takže nová tabulka bez tenantu test shodí sama.
- **Poprvé testy proti skutečné databázi** (Testcontainers). Unikátní klíče a souběh (dva běhy rozhodnou stejnou platbu, dvě první přihlášení naráz) nejde poctivě otestovat nad in-memory providerem.
- OIDC bez sítě a bez tajných klíčů: test podstrčí metadata poskytovatele a kontroluje přesměrování (autorita, `client_id`, scope, PKCE, callback). Skutečné přihlášení přes Microsoft/Google ověří až vývojář s vlastní registrací aplikace.
- Drobnosti, co agent pokazil:
  - jednorázová sonda „je tu python?“ přes heredoc spustila na Windows interaktivní REPL a visela 2 minuty → zastaveno, dál jen Write/Edit,
  - `sed` se zpětnými lomítky v cestě csproj počtvrté tiše nic nenahradil; tentokrát hned vidět v kontrolním výpisu, opraveno přes Edit,
  - pomocník testů ořízl jméno databáze `[..40]` z řetězce, který byl kratší → 9 testů spadlo na `ArgumentOutOfRange`, ne na izolaci,
  - minimal API neumí navázat `?full=1` na `bool?` → 400; chytil test,
  - Npgsql si přitáhl EF Core 10.0.4, nástroj `dotnet-ef` byl 10.0.3 → sjednoceno.
- Provozní: běžící `payments-mcp` (z `.mcp.json`) zamyká Debug výstup, build padal na zamčených DLL. Lokálně proto build/test v Release; zapsáno do AGENTS.md.

**Fáze 8 (2026-11-12)**
- Plán znovu vznikl v nové session jen z PLAN/SPEC/ADR a kódu, schválen beze změn.
- **Nejdůležitější návrhové rozhodnutí nebylo v SPEC:** „validace jako u modelu“ doslova by frontu znehodnotila – člověk tam je právě kvůli případům, které validace neprojdou (částečné úhrady, třetí strany). Rozdělení na blokující chyby a varování, která se odškrtávají jednotlivě, a server chce přesně aktuální množinu (zastaralá stránka nebo ručně poskládaný požadavek nic nepřeskočí).
- **Skrytá vazba mezi fázemi:** po lidském spárování by denní job (fáze 9) spadl na primárním klíči `invoice_matches`, protože `DbDecisionStore` znal jen automatická spárování. Ošetřeno a pokryto testem dřív, než job existuje.
- Testy datové vrstvy prošly napoprvé; HTTP testy až na jedno špatné očekávání v testu (hledal štítek z řádku seznamu na stránce detailu).
- Drobnosti, co agent pokazil:
  - přepsal `Pages.cs` novým obsahem a s ním i třídu `SecurityHeaders`; všiml si hned po zápisu a vrátil ji do vlastního souboru,
  - proměnná `code` v Razoru koliduje s direktivou `@code` (chytil build),
  - shell skript s několika heredocy se celý nepřeložil a nezapsal nic → zbytek přes Write,
  - test MCP předpokládal kontrolu `INVOICE_ALREADY_MATCHED`, ale server obsazenou fakturu odmítne už dřív výjimkou; opraveno před spuštěním,
  - **nový MCP test závisel na náhodě:** bral „druhou otevřenou platbu“ ze startovních dat sandboxu, jejichž seed je odvozený z náhodného id tenantu. Lokálně prošel, v CI data otevřené platby neměla → `ArgumentOutOfRange`. Oprava: test si potřebné platby a rozhodnutí vloží sám. Ponaučení: test nad generovanými daty buď s pevným seedem, nebo s vlastními řádky.
- Provozní: lokálně běžela starší verze webu z jiné session na 7180, nová verze se ověřovala na 7181 (stejná DB, migrace navíc jen přidá tabulku).

**Změna SPEC před fází 9 (2026-11-12)**
- Vývojář před fází 9 zpochybnil „5–10 nových plateb denně v každém sandboxu“: veřejná ukázka nemá pálit tokeny na data, na která nikdo nekouká. Agent spočítal cenu z evalů (≈ $0,0008 za AI platbu, původní plán ~$1,5–12 měsíčně podle počtu uživatelů) a navrhl doplňování podle aktivity, sdílenou denní dávku s cache odpovědí (cena nezávislá na počtu uživatelů), tři vrstvy rozpočtu a režim „jen malý model“. Schváleno celé, včetně dema bez přihlášení a `/admin` jen se souhrny. Zapsáno do SPEC 9b a ADR 0013.
- Postřeh: cache odpovědí je jen produkční využití record/replay z fáze 4. Nástroj postavený kvůli testům bez API klíče se vrací jako úspora v provozu.

**Fáze 9 (2026-11-12)**
- Plán znovu vznikl v nové session jen z PLAN/SPEC/ADR a kódu. Agent se před schválením zeptal na jedinou věc, kterou ADR 0013 nedořešil: nový sandbox má ve startovních datech ~5–7 AI plateb, ale strop 2 volání denně. Doslovné čtení („člověku se štítkem“) by novému uživateli hned ukázalo 3–5 štítků „rozpočet vyčerpán“. Vývojář zvolil: nad strop sandboxu se čeká na další den, štítek jen pro globální rozpočet.
- **Měření místo předpokladu:** první verze dávky brala „pravidlové“ případy podle intuice. Test přes 400 dní selhal (3 platby pro model místo 1–2); jednorázová sonda ukázala, že `MultiInvoice` jde do AI vždy a částečné úhrady a VS ve zprávě jen někdy. Pooly dávky upraveny podle čísel a test to drží.
- Druhý nález z testu: injection bez kandidátů rozhodne politika bez modelu, takže „AI případ“ neznamená vždy „volání modelu“. Testy cache si proto vybírají den, kdy dávka model opravdu potřebuje.
- **Past v rozpočtu odhalená při návrhu:** gateway kontroloval strop *před* voláním poskytovatele, takže při vyčerpaném stropu by nepustil ani zdarma dostupnou odpověď z cache. Doplněno `IAnswerCache` + test „přes strop se cached odpověď použije, model ne“.
- Riziko, které agent ošetřil předem: cache sdílená mezi `stub` a `live` by v lokální DB uložila odpověď stubu pod klíčem skutečného modelu a pozdější živý běh by ji vrátil. Klíč cache má proto i zdroj.
- Drobnosti, co agent pokazil:
  - shell skript s několika heredocy se znovu nepřeložil a nezapsal nic → Write,
  - `sed` se zpětnými lomítky v cestě csproj popáté tiše nic nenahradil (odhalil to `grep` hned po něm) → Edit,
  - `--` v XML komentáři csproj podruhé (build),
  - test s pořadím GUID by procházel náhodou (porovnání seřazené množiny se seznamem v pevném pořadí) – opraveno před commitem,
  - spojení dvou interpolovaných řetězců přes `+` uvnitř `FormattableString.Invariant` neprojde překladem (výsledek je `string`).
- Po fázi vývojář zpochybnil limit 2 volání na sandbox a den: na dashboardu „2 / 2“ vypadalo směšně, když jedno volání stojí zlomek centu, a nové sandboxy čekaly na AI několik dní. Ochranu proti „oškubání“ drží globální stropy a počet nových sandboxů, ne limit sandboxu. Změněno: 20 volání na sandbox a den, 5 nových sandboxů denně (dodatek v ADR 0014). Pro nasazení doporučen měsíční rozpočet $0,50 a spend limit $5 v Anthropic konzoli.

**Fáze 10 (2026-11-12)**
- Plán vznikl v nové session z PLAN/SPEC/ADR a kódu, schválen beze změn.
- **Hlavní nález z testu, ne z návrhu:** MCP SDK u nástroje, který skončí chybou, dává do statusu spanu celou odpověď nástroje – a ta opakuje argumenty od agenta (např. neznámé id). Test s označenými argumenty to chytil napoprvé; redakční procesor proto maže popis chyby u všech cizích spanů. Ponaučení: „instrumentace knihovny nezapisuje argumenty“ neznamená, že je nezapíše jinudy.
- Návrhové rozhodnutí: knihovny jen na BCL `System.Diagnostics`, SDK jen v hostitelích. CI tak nepotřebuje backend ani balíček navíc a testy čtou spany přes `ActivityListener` filtrovaný podle vlastního trace (testy běží paralelně, posluchače jsou globální).
- Replay hlásí cenu a latenci z nahrávky (jako audit). Proto je v spanu i `payments.ai.latency_ms` – délka spanu při replay je zlomek milisekundy.
- Drobnosti, co agent pokazil:
  - nový projekt `PaymentMatching.Telemetry` zastínil třídu `Telemetry` v namespace `PaymentMatching.*` (build) → přejmenováno na `PaymentMatching.Observability`,
  - webový test telemetrie prošel samostatně, v celé sadě ne: instrumentace ASP.NET Core poslouchá celý proces a viděla požadavky paralelních testů (`Assert.Single` našel 6 přihlášení). Oprava: test hledá jen své unikátní cesty a značky, nikdy počty,
  - poslední požadavek testu: span na test serveru končí až po předání odpovědi → krátké čekání na span,
  - analyzátory (IDE0062 statická lokální funkce, xUnit2029 `Assert.Empty` nad filtrem) – build.
- Během fáze vývojář doplnil do AGENTS.md pravidlo pro sdílené soubory (case-study-notes jen přidávat na konec, před úpravou znovu načíst). Agent tu změnu nechal necommitnutou (není jeho) a do indexu přidal jen svůj řádek s příkazy telemetrie.


**Fáze 11 (2026-11-12/13)**
- Plán vznikl v nové session z PLAN/SPEC/ADR, kódu a jen čtení existujících prostředků v Azure. Dvě rozhodnutí vývojáře: sdílený PostgreSQL server JobScoutu (vlastní DB a role bez admin práv) a image na ghcr.io jako veřejné balíčky. Hosting tak vyšel ~$0–2 měsíčně místo ~$20.
- **Bezpečnostní kontrola Claude Code agentovi nepovolila aplikovat infrastrukturu** (`az deployment`), i když vývojář krok schválil. Bicep proto spouštěl vývojář sám; bootstrap DB, ruční běh jobu a nastavení GitHubu agent provedl. V praxi dobré rozdělení: změny prostředků dělá člověk, agent píše kód a ověřuje.
- **Čtyři chyby, které ukázalo až první nasazení (žádný test je nemohl chytit):**
  1. Azure Flexible Server zakládá schéma `public` nové databáze s vlastníkem `azure_pg_admin` → první migrace „permission denied“. Bootstrap teď schéma předá roli aplikace.
  2. Druhý běh bootstrapu vygeneroval nové heslo, ale Container Apps drží načtenou hodnotu tajemství z Key Vaultu až do nové revize (restart nestačí) → web i první běh jobu spadly na přihlášení do DB (job před voláním modelu, $0). Skript heslo bez `RESET_PASSWORD=1` nemění.
  3. Exporter Azure Monitor si sám přidá vzorkování s limitem za sekundu; krátký proces (job, CLI) má jediný trace a ten zahodil. Metriky chodily, spany ne. Diagnostika SDK („Sampler added = RateLimitedSampler“) → pevný poměr 1,0.
  4. GitHub posílá v OIDC tokenu `subject` s neměnnými id vlastníka a repa (`repo:owner@id/repo@id:…`); federated credential podle starého formátu nic nenašel. Subject je parametr.
- `/api/me` neukazoval `provider:subject`, který `/admin` potřebuje – vývojář by své id neměl jak zjistit. Doplněno.
- Drobnosti, co agent pokazil:
  - šestkrát stejná past s Windows shellem: sonda „je tu python?“ s heredocem a `cat` bez vstupu visela 2 minuty → zastaveno, dál Edit/Write,
  - argumenty `az … firewall-rule` (`--server-name` vs `--name`) – opraveno před spuštěním podle nápovědy,
  - gitleaks označil veřejná id vestavěných rolí Azure v Bicepu jako klíče (CI červené) → přejmenování proměnných a `.gitleaksignore` pro první commit,
  - vývojáři v PowerShellu `bash` spouští WSL (chyba 0x8007274c) → návod přes Git Bash,
  - odhad prvního živého běhu (< $0,01) byl nízký: $0,031, demo dostalo startovní data i dávku najednou a část volání eskalovala na Opus,
  - jeden běh celé sady testů měl jeden pád, opakování prošlo – nestabilní test, nedohledán.
- **Produktový nález vývojáře po spuštění:** nový uživatel uvidí AI platby až po ranním jobu, takže první dojem je „čeká na AI“. Pravidlo „návštěvník nikdy nespustí model“ přitom platí. Návrh: sdílená startovní data a cache odpovědí, sandbox dostane AI rozhodnutí z cache při přihlášení bez volání modelu (ke schválení, změna SPEC 9b / ADR 0011).

**Po fázi 11: AI hned po přihlášení (2026-11-13, ADR 0017)**
- Vývojář po prvním přihlášení v produkci: „AI platby čekají do rána – to je k ničemu, uživatel musí AI předpárování vidět hned.“ Pravidlo „návštěvník nikdy nespustí model“ přitom zůstává.
- Řešení schválené vývojářem: startovní data stejná pro všechny sandboxy založené v daném týdnu (seed = pondělí), job je jednou týdně předpočítá v paměti a přihlášení vezme AI rozhodnutí z cache odpovědí s rozpočtem 0 volání (za cache je poskytovatel, který vyhodí výjimku). Vývojář výslovně chtěl ověřit, že data zůstanou oddělená: test rozhodne platbu v sandboxu A a kontroluje stejnou platbu v B.
- Klíčový předpoklad (stejné prompty při přihlášení i v předpočítání) drží jedna společná metoda `DecideStarterAsync` a test „po jobu má přihlášení všechny AI platby z cache a model nedostal žádný dotaz“.
- Record/replay z fáze 4 se tak vrací potřetí: testy bez klíče → cache v provozu → okamžitý první dojem za $0.
- Útrata: ruční běh jobu pro dva sandboxy vývojáře před změnou $0,017 (12 volání); první předpočítání startovní sady (týden od 2026-11-09) se souhlasem vývojáře $0,029 (9 volání, všech 20 plateb rozhodnuto). AI za fázi 11 a úpravy celkem $0,077.

**Po fázi 11: úpravy UI z provozu (2026-11-13)**
- Vývojář při první práci ve frontě (`FIELD/ADV_OVERPAY`, přeplatek zálohy o 100 Kč) postrádal fakturu vedle platby: detail ukazoval jen číslo faktury, plné údaje byly až na stránce ručního párování. Nová karta „Invoices“ v detailu: všechny faktury, které jmenuje rozhodnutí, lidské rozhodnutí nebo návrh (druh, VS, odběratel, splatnost, částka, stav) a porovnání platby s každou jmenovanou sadou („+100,00 CZK paid more than invoiced“). Test: změna faktury se stejným číslem v cizím sandboxu se v panelu neprojeví.
- Záložka Admin v horní liště jen pro účty z `Admin:Accounts`; `/admin` má teď vlastní navigaci (test: běžný uživatel odkaz nevidí).
- Přihlašovací stránka podle Job Scoutu: karta s tlačítky přes celou šířku a logy (Continue with Microsoft / Google), oddělovač „or“ a demo bez přihlášení. Loga jako inline SVG s atributy `fill` (CSP nepovoluje inline styly).
- Agent lokálně: `localhost` se na stroji vývojáře přeložil na IPv6, kde DB neposlouchá („connection reset“) → při ruční kontrole `127.0.0.1`. Existující test admin stránky zakazoval slovo „boss“ – po přidání navigace je tam jméno samotného admina; test upraven tak, aby dál hlídal data ostatních.
- **Seznam účtů v `/admin`** (dodatek ADR 0014): vývojář chtěl vidět, kdo se přihlásil. Schvalování přihlášek jako v Job Scoutu zamítnuto společně – návštěvník nespustí model, nové sandboxy jsou omezené a AI má strop, neznámý účet stojí jen řádky v DB. Karta „Accounts“: jméno, e-mail, poskytovatel, založení a poslední aktivita, počty potvrzení / zamítnutí / ručních párování, stav (aktivní / smaže se za N dní). Obsah sandboxů ani id tenantů ne; test vyjmenovává přesná pole `AccountSummary`, webový test hlídá, že na stránce nejsou texty plateb ani jména protistran. Doplněno do `/privacy` (co vidí provozovatel) a SPEC 9b. Agent při úpravě testu skriptem omylem smazal `[Fact]` následujícího testu – všiml si v kontrolním výpisu před buildem.
- **Před ukazováním aplikace** (vývojář ji chce posílat ještě před zveřejněním repa): `/privacy` nově uvádí správce údajů a kontakt pro dotazy a smazání účtu (`Operator:Name`, `Operator:Email`, GDPR); limit nových sandboxů 5 → 30 za den (díky týdenní startovní sadě bez ceny za AI); web s jednou stále běžící replikou místo scale-to-zero (bez studeného startu, ~$3–5 měsíčně navíc). Vše jako parametry v `infra/main.bicepparam`.

**Po fázi 11: druhé uživatelské testování (2026-11-13, ADR 0018)**
- Vývojář jako uživatel: chybí favicon; ikony na úvodní stránce v rohu rámečku; chce odkaz na svůj web; aplikace vypadá prázdně (2 ve frontě); „AI calls 6“ nesedí na 7 plateb od Haiku; není jasné, co přesně udělala AI a jestli jde zvrátit; kódy pravidel (`FIELD/EXACT`) nikdo nepochopí. K tomu stránkování, řazení, hledání a čeština.
- Odpovědi z kódu, ne odhadem: 6 vs. 7 = šest placených volání + jedna odpověď z cache (dashboard počítal jen placená volání); ikony = selektor `.flow span` přebil `display: grid` rámečku ikony.
- Vzniklo: karta „What AI added“ (rozhodla AI / spárovala sama / poslala člověku / zastavené injection / placená volání vs. cache / **přesnost AI očima lidí**), rámeček „Decided with AI“ v detailu, filtr „Decided by AI“, **vrácení párování AI** (`/payments/{id}/undo`, rozhodnutí člověka `undone_ai`, audit AI zůstává, uvolněnou fakturu pipeline sama znovu nepáruje), lidské popisy všech kódů pravidel + stránka `/how`, seznam plateb s hledáním (plátce, VS, zpráva, částka, id), filtry, řazením a stránkováním po 25 (na serveru, bez JS), **čeština** (`Text.T`, přepínač EN/CS v hlavičce, cookie, test vykreslí všechny stránky česky a selže na chybějícím překladu), favicon, odkaz „About this project“ (`aboutUrl` v Bicepu), startovní sada 40 plateb.
- Drobnosti, co agent pokazil / co chytil test:
  - heredoc s Pythonem v shellu znovu selhal (neuzavřená uvozovka) → skripty do souboru,
  - Razor: `@page` a `@code` jako názvy proměnných (direktivy) – dvakrát,
  - Razor kódoval české znaky jako `&#x159;` (prohlížeč to zobrazí, test ne) → `WebEncoderOptions` s celým Unicode,
  - legenda pravidel měla kód `FIELD/AMOUNT`, který pravidla nikdy nevrátí, a chyběl `MSG/ADV_OVERPAY` – opraveno podle kódu `PaymentMatcher`,
  - test s návrhem agenta předpokládal, že návrh v datech ještě není; se 40 platbami už byl → test ho nezakládá dvakrát,
  - nestabilní test: `McpTelemetryTests.Tool_calls_are_traced_by_name_without_arguments_or_error_text` občas padá v celé paralelní sadě, samostatně prochází (k dořešení).
- **Rezerva rozpočtu na volání:** první předpočítání startovní sady se 40 platbami skončilo po 8 voláních ($0,0031), 6 plateb čekalo. Příčina: měsíční rozpočet drží rezervu $0,05 na volání (ADR 0014), takže při ~$0,42 zbývajících smí běh jen 8 volání, i když skutečné volání stojí $0,0004–0,01. Rezerva je nově parametr Bicepu `aiReservePerCallUsd` = $0,02 (strop $0,50 měsíčně platí dál). Útrata za předpočítání: $0,0031 + $0,0020 po změně rezervy (8 odpovědí z cache, 6 nových volání, všech 40 plateb rozhodnuto).
