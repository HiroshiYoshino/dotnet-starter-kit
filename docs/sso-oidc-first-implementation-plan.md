# OIDC 先行・将来 SAML 対応を見据えた SSO 実装計画

作成日: 2026-10-15

ステータス: 実装前の計画。型名・API・配置は提案であり、機能や接続検証の完了を示さない。

## 1. 本計画の位置付け

既存の `docs\sso-oidc-saml-implementation-plan.md` は、OIDC と SAML 2.0 の両方を初期リリースの完了条件とする計画として残す。本ファイルは、**OIDC 単独で先にリリースし、SAML の実装・接続検証を待たない場合の代替実行計画**である。両計画のフェーズ番号は独立している。

本計画を採用した場合、初期の実装範囲・完了条件は本ファイルに従う。共通の安全要件は元計画を引き継ぐが、元計画にある SAML ライブラリ導入、ACS、SP metadata、証明書管理、SAML 試験は OIDC リリースの前提にしない。

| 観点 | 両方式対応計画 | 本計画 |
| --- | --- | --- |
| 最初の製品リリース | OIDC + SAML | OIDC のみ |
| 最初の実接続検証 | Entra OIDC / SAML | Entra OIDC |
| SAML の扱い | 初期実装の必須機能 | 将来の独立した追加リリース |
| 最初に共通化するもの | 両 protocol の認証後処理 | tenant / connection / 外部 identity / アプリログイン完了の境界 |
| SAML 用の先行実装 | 初期範囲に含む | 実装しない。必要な拡張点だけ文書化 |
| 顧客への説明 | 検証済みの両方式に対応 | 「OIDC SSO 対応」。SAML 対応予定を対応済みと表示しない |

想定は一般企業向け B2B SaaS、初期ログイン対象は `clients\dashboard`、接続設定の初期管理者は `clients\admin` の認可済み operator とする。root operator の SSO ログインは対象外。1 テナントにつき有効な接続は 1 件とし、別テナントは異なる IdP を使用できる。

## 2. 初期リリースの範囲

### 実装するもの

- OIDC Authorization Code Flow + PKCE によるバックエンド confidential client。
- テナント別の OIDC 接続設定、設定の検証・有効化・更新・無効化、secret の write-only 更新。
- tenant identifier による明示的な Home Realm Discovery。
- SaaS 側から開始するログイン、事前連携済みの既存ユーザーの認証。
- 明示的なアカウント連携・解除。メール一致だけで連携しない。
- 既存アプリの JWT / refresh / session / permission hydration への接続。
- ローカル MFA の維持、接続別監査、鍵 rotation、複数 API instance での動作。
- Entra OIDC の実接続検証と controlled OIDC fixture / Keycloak による自動試験。
- dashboard のログイン・callback・MFA と、operator 向けの最小接続管理 UI。

### 後続へ送るもの

| 機能 | 初期実装で行わないこと |
| --- | --- |
| SAML 2.0 | package、ACS、AuthnRequest、metadata、XML 署名検証、SAML 証明書 UI を追加しない |
| JIT / SCIM | 未連携ユーザーを自動作成しない。退職・異動の自動同期を約束しない |
| SSO 必須化 | Optional のみ。Required の設定や見せかけの enforcement を公開しない |
| 複数有効 IdP | 同一 tenant の複雑な IdP 選択・優先順位・fallback を実装しない |
| メール domain discovery | domain 所有確認と tenant catalog 検索を導入しない |
| IdP 固有の権限連携 | IdP group / role をそのままアプリ権限にコピーしない |
| IdP logout / SLO | 全 IdP session の終了を保証しない。アプリ側 session / refresh の失効と token 消去を対象にする |
| その他 | LDAP、WS-Federation、CAS、Entra Graph API、全アプリの BFF 移行を含めない |

SAML 要求が導入条件の顧客は初期対象外とし、OIDC の先行リリースで要件を満たしたことにしない。

## 3. リポジトリの既存実装・指示への適合

所属は新 Module ではなく既存 Identity の拡張とする。以下は元計画で確認した既存接続点であり、実装時に再確認する。

| 接続点 | 実装方針 |
| --- | --- |
| `Modules.Identity\IdentityModule.cs` | SSO service / OIDC handler / 管理 endpoint を登録 |
| `Authorization\Jwt\JwtAuthenticationExtensions.cs` | API の default authenticate / challenge は JWT Bearer のまま。401 を IdP redirect にしない |
| `Services\IdentityService.cs` | account eligibility と通常ログインの claim 構築を必要な範囲で共有 |
| `Services\UserRegistrationService.cs` | メール一致で既存 user を返す `GetOrCreateFromPrincipalAsync` を SSO 連携に流用しない |
| `Domain\FshUser.cs` | `ObjectId` に全 IdP の識別子を詰めず、外部 identity mapping を別 entity にする |
| `Data\IdentityDbContext.cs` | 既存 Finbuckle Identity context に接続設定と mapping を追加 |
| `Features\v1\Tokens\TokenGeneration\` / `RefreshToken\` | JWT 発行、refresh hash 保存、session、監査、event を再利用 |
| Api `Program.cs` / `BuildingBlocks\Web\Extensions.cs` | callback の tenant 解決を `UseAuthentication()` 前に準備する必要がある |
| dashboard の `auth-context.tsx` / `token-store.ts` | token 受領後の認証状態・permissions・realtime 更新に接続 |

`BuildClaimsForUserAsync` は impersonation 用で、通常ログインと同じ tenant eligibility / group role 処理ではないため、そのまま SSO 完了処理にしない。既存 password / refresh / impersonation の意味を変える大規模リファクタリングもしない。

実装前に `AGENTS.md`、`.agents\rules` の architecture、api-conventions、database、security、identity / multitenancy、resilience、localization、testing / integration-testing、frontend 各ルールを読む。作業に応じて `add-feature`、`add-entity`、`create-migration`、`add-permission`、`add-full-slice`、`testing-guide`、`mediator-reference` を使用する。

- request / DTO は Contracts、handler / validator / endpoint は runtime の同一 Feature に配置する。
- Mediator handler は `public sealed`、`ValueTask<T>`、await は `.ConfigureAwait(false)`。command と paginated query に validator を用意する。
- 他 Module との連携は Contracts / 既存 framework interface に限定する。
- 新 entity に tenant isolation を適用する。IdentityDbContext は `BaseDbContext` 派生ではないため、既存の model 構築順と `ApplyTenantIsolationByDefault()` を維持する。
- migration は中央 PostgreSQL Migrations の Identity フォルダーへ追加し、DbMigrator で適用する。API startup で migrate しない。
- BuildingBlocks は無承認で変更しない。Host / Identity 内の変更で成立するか先に検証し、必要なら明示的承認を得る。
- 新 Module を作らないため、Module 追加に伴う 4 か所の assembly 登録は不要。既存 Identity の service / endpoint 登録は更新する。
- localization、構造化ログ、CancellationToken、TimeProvider を使う。UI は各 app の API client・permission・routing・form の既存規約を守る。

## 4. 今作る共通部分と、SAML まで作らない部分

### 4.1 protocol に依存させない境界

認証方式が異なっても、外部認証の検証後は同じアプリログイン完了処理を使えるようにする。

| 共通部分 | OIDC での利用 | SAML 追加時 |
| --- | --- | --- |
| `SsoConnection` | tenant / protocol / 接続 ID / 有効状態 / version | 同じ接続識別・管理規約を利用 |
| 外部 identity key | 検証済み issuer + sub | IdP entity ID + 不変の subject を利用 |
| login transaction | tenant / connection / app / purpose / browser binding / handoff | 共通 envelope に SAML 固有 correlation 情報を追加 |
| account link | 明示的に内部 User と外部 identity を結び付ける | 同じ連携・解除・監査方針を利用 |
| eligibility / MFA | user / tenant / connection の有効性、必要なローカル TOTP | 検証済み SAML identity にも同じ処理 |
| sign-in completion | アプリ JWT / refresh / session / event / audit | 再利用し、SAML 専用 JWT を作らない |
| SPA handoff | 一回限りの code 交換 | protocol を意識しない callback / token 受領を維持 |

検証済み外部 identity は runtime 内の小さな型として表現する。候補名は `VerifiedExternalIdentity` とし、tenant、connection、protocol、issuer、subject、認証時刻、承認済みの認証強度情報を持たせる。ブラウザーからこの型や任意の ClaimsPrincipal を受け取って「検証済み」と扱う API は作らない。生成元は protocol 検証を通過した adapter に限定する。

外部 claim の生データはアプリ JWT にコピーしない。ユーザー属性と権限の source of truth は内部 Identity とする。

### 4.2 過剰な先行抽象化を避ける

最初は OIDC adapter 一つで実装する。未使用の SAML handler、throw-only の実装、汎用 plugin registry、protocol ごとの空 project、SAML package への依存は作らない。実装が一つしかない段階で、全 protocol を包む大きな interface を設計しない。

共通ログイン完了処理に OIDC の `OpenIdConnectOptions`、IdToken、nonce、code verifier を渡さず、検証後の identity と transaction を渡すところまで分離する。将来 SAML adapter を追加するときに必要な interface が明らかになった場合、その時点で抽出する。

SAML 用 column や DTO を大量に nullable で先行追加するのではなく、OIDC 設定を共通接続情報から分離する。protocol 種別の保存形式は将来追加できる安定した値にし、**初期 API / validator / 実行経路は OIDC 以外を明示的に拒否する**。選択不能な SAML の設定欄や成功扱いの仮 endpoint は公開しない。

## 5. データモデルと接続ライフサイクル

| 提案 entity / store | 初期実装 |
| --- | --- |
| `SsoConnection` | TenantId、Id、Protocol、DisplayName、Enabled、ConfigurationVersion、検証状態、監査属性 |
| OIDC 固有設定 | ConnectionId、Authority、期待 issuer、ClientId、secret 参照、許可する外部組織識別。typed な owned configuration または関連 entity として分離し、PoC で形を確定 |
| `ExternalIdentityLink` | TenantId、ConnectionId、Protocol、Issuer、SubjectKey、UserId、連携者・日時。SAML 固有識別情報は将来 migration で追加 |
| `UserSession` への追加属性 | 認証方式、ConnectionId、認証時刻。SSO refresh と接続停止の再評価に使う |
| transaction / handoff store | purpose、tenant、connection、version、app、許可済み return path、期限、binding、状態、一回性 |

初期の Optional 方針はサーバーで固定し、未使用の Required / JIT policy engine を作らない。将来必要になった時点で `TenantSsoPolicy` を追加する。

接続と mapping は IdentityDbContext に保持し、shared DB / per-tenant DB の既存構成に従う。tenant 解決前に全 tenant の Identity DB を横断検索する仕組みは追加しない。外部 key は `(TenantId, ConnectionId, Protocol, Issuer, SubjectKey)` を単位に一意にし、user / connection の tenant 整合性も保証する。issuer / sub を小文字化して同一視しない。index に hash を使う場合は元値の完全一致も検証する。

初期の「有効接続は tenant ごとに 1 件」は、並行更新でも破れない DB 制約を含めて実装する。将来 SAML 接続へ切り替える際も、無効状態の事前登録・検証・切替を可能にする。複数同時有効化を始める場合は別の migration と選択 policy を用意し、制約を黙って外さない。

接続更新は concurrency token / version で競合を検出し、全 instance の scheme / options / metadata cache を失効させる。Authority、issuer、ClientId の変更は identity の信頼境界の変更として再検証・必要な再連携を要求する。secret rotation は同じ identity を維持できるが、古い transaction の扱いを明示する。接続無効化・秘密漏えいでは進行中 transaction も拒否する。

## 6. OIDC の実装上の要点

### 6.1 protocol と動的設定

`Microsoft.AspNetCore.Authentication.OpenIdConnect` を第一候補にし、中央 package 管理と既存 Microsoft.IdentityModel 依存を確認して採用 version を固定する。署名・Discovery・code flow を自作しない。

- Authorization Code Flow、PKCE S256、state、nonce、client 認証を使用する。implicit / password grant は使わない。
- scope は原則 `openid profile email`。IdP refresh token / Graph API / downstream API access は要求しない。
- issuer、署名、audience、期限、nonce、必要な azp を検証する。userinfo を使う場合は sub の一致を検証する。
- Entra は顧客 directory に固定した authority / issuer を初期案とする。`common` を無制約に受け入れない。
- Discovery / JWKS / token / userinfo / PAR の全通信先に HTTPS、SSRF・DNS rebinding・redirect 制限、timeout、payload 上限を適用する。
- unknown kid の JWKS 再取得には回数制限を設け、rotation と IdP 障害を試験する。
- metadata の GET と異なり、token / PAR の POST に汎用の自動 retry を無条件適用しない。.NET handler の PAR 動作も接続先で確認する。

tenant / connection / configuration version 単位の named options を用意し、共有 options の Authority / ClientId / secret をリクエストごとに書き換えない。version を分けても IdP 登録済み callback URI は固定する。

同じ callback path を旧新 scheme が取り合わないよう、transaction に対応する設定 version の handler が一つだけ処理することを PoC の必須項目にする。最初に列挙された remote handler が別 version の state を処理して失敗する構成は採用しない。scheme 数・options cache は上限と削除方針を持ち、callback を別 instance が受けたときも再構成できるようにする。

### 6.2 callback 前の tenant scope

ブラウザーの IdP callback は `tenant` / `X-FSH-App` header を送れない。URL の tenant / connection は設定候補の識別にだけ使い、最終 tenant は保護済み state とサーバー transaction から確定する。開始時と異なる tenant / connection / version / app は拒否する。

既存 `UseAuthentication()` は Module middleware より前にある。Module-owned bootstrap を Api Host に接続し、認証 handler より前に設定候補と必要な scheme を準備する案を PoC する。IdentityDbContext / UserManager は tenant context 設定後に解決し、singleton に scoped service を保持しない。

既存 composition では `UseHeroMultiTenantDatabases()` 後、`UseHeroPlatform()` 前への接続が候補になる。この位置は platform の exception / forwarded headers / rate limiting より前なので、bootstrap は限定的な準備だけを行い、自身の異常を安全に記録・拒否する。public callback URL は固定の設定から生成し、未検証 Host / scheme に依存させない。remote handler による short-circuit を考慮し、callback の回数制限・監査・安全な error response を別途保証する。

既存 Bearer の tenant や root header override が付いた callback でも、transaction の tenant を別 tenant に変えない。tenant context は同じ async 実行経路で設定し、shared DB と dedicated DB で試験する。

### 6.3 SPA 開始・callback・handoff・MFA

API fetch のレスポンスで IdP に 302 を返すだけでは、トップレベルの画面遷移にならず CORS 問題も起きる。初期フローを以下のように定義する。

1. dashboard はタブ別の短期 verifier を生成し、challenge、tenant、connection、app、return path を開始 API に POST する。開始 API は browser binding と transaction を作り、一回限りの launch URL を JSON で返す。
2. SPA は launch URL にトップレベル遷移する。API の launch 処理が transaction / browser binding を確認し、OIDC Challenge を実行して correlation / nonce cookie を設定し、IdP へ redirect する。launch URL 自体も短寿命・一回限りにする。
3. OIDC handler は callback で protocol と transaction を検証し、内部の検証済み identity と handoff code を生成する。通常の application cookie sign-in へ流さず、専用完了処理で応答を終了する。
4. 登録済み SPA callback に opaque handoff code のみ渡す。SPA は履歴から code を除き、code / verifier を交換 API に POST する。OIDC のバックエンド PKCE と、この SPA handoff 用 verifier は別物として保存・検証する。
5. code を原子的に消費する。ローカル MFA が必要なら、JWT の代わりに短期 MFA continuation を返す。TOTP 完了後にのみ token を発行する。
6. 最終的な token 受領を AuthProvider の共通 session 設定に接続し、permissions hydration と realtime 再接続を既存同様に行う。

access / refresh / ID token を URL に載せず、IdP token を SPA に保存しない。redirect URI は登録済み app origin と許可した path から組み立て、任意 return URL、scheme-relative URL、二重 encoding による回避を拒否する。

launch / handoff / MFA continuation は purpose、tenant、connection、app、browser binding、期限に束縛する。並行消費は Redis / DB の原子的処理で一回だけ成功させる。`IDistributedCache.Get` → `Remove` で代用しない。store 障害時に instance-local cache へ fallback せず、SSO は fail closed。失敗後の code を復活させず再ログインを案内する。

binding cookie は HttpOnly / Secure、必要最小限の path / lifetime とする。初期 OIDC は GET callback を基本に、各 cookie の SameSite を実ブラウザーで確認する。将来の SAML POST のために全 cookie を先に SameSite=None へ変更しない。開始・交換の credentialed fetch は許可済み Origin のみとし、credentials を全 API に一括追加しない。

初期配備は API / dashboard が同一 site または reverse proxy 配下を推奨し、cross-site 配置は cookie 制限を検証してからサポート対象を定める。Data Protection key ring と transaction store は複数 instance で共有・永続化する。

暫定 TTL は login transaction 5 分、handoff 60 秒。MFA continuation と launch の TTL・回数上限は PoC で定め、TimeProvider による境界・再送・同時タブ試験を行う。

### 6.4 アカウント・権限・session

既存ユーザーへの事前 mapping を基本とする。初期 operator の明示的登録・解除 API を用意し、認証済み本人連携を追加する場合は直近再認証と必要な MFA を要求する。本人連携の transaction は通常 login と purpose を分け、開始時の user を固定する。

`email_verified=true` でもメール一致による既存アカウントの自動連携はしない。未連携は拒否し、停止ユーザーの再作成・再有効化はしない。最後のログイン手段を消す unlink は防ぐ。

token 発行直前に user / tenant / 接続の有効性、契約猶予、メール確認状態を既存方針に従って再検証する。claim は内部 User、直接 role、group role、locale から生成する。ローカル `TwoFactorEnabled` は維持し、IdP の amr / acr を無条件にローカル MFA の代替としない。

SSO でも初回 `TokenResponse` と既存 refresh の別契約を維持する。SSO session 作成失敗では token を返さず、既存ローカルログインの best-effort session 動作は変えない。refresh は保存済み session の認証出自と ConnectionId を基準に接続停止・user / tenant 停止を再評価し、未検証 access token の claims だけで許可しない。

既存 FshUser の refresh token 保存はユーザーにつき一つである。先行 SSO と同時に device 別 refresh token 基盤へ作り替えず、複数ログイン時の既存 rotation 挙動を明示して回帰試験する。監査と token event は既存経路・Outbox を利用し、新規 Module Outbox を作らない。

アプリ側 logout は session / refresh の失効と browser token 消去を実装範囲にする。外部 IdP session と発行済み JWT の即時失効は別問題であり、IdP 無効化・接続停止から access token が失効するまでの許容時間を利用ガイドに記載する。初期は既存 JWT TTL の限界を明示し、即時失効を約束しない。

## 7. API・配置・管理 UI

以下は案。通常 API は Identity の version group、IdP 登録 callback は version 変更で壊さない固定 URI とする。

| 操作 | 提案ルート / 条件 |
| --- | --- |
| discovery | `POST /api/v1/identity/sso/discover`。匿名。公開 label / protocol / connection public ID のみ |
| start | `POST /api/v1/identity/sso/start`。匿名。transaction を作り launch URL を返す |
| launch | `/auth/sso/{tenantPublicId}/{connectionPublicId}/oidc/start`。トップレベル GET、一回限りの launch proof |
| OIDC callback | `/auth/sso/{tenantPublicId}/{connectionPublicId}/oidc/callback`。remote handler が処理。Mediator endpoint と二重 map しない |
| exchange / MFA | `POST /api/v1/identity/sso/exchange`、`/complete-mfa`。短期 proof 必須 |
| 接続管理 | `/api/v1/identity/sso/connections` と ID route。専用 permission |
| 接続検証 | `POST /api/v1/identity/sso/connections/{id}/test`。設定診断と実ログイン検証を区別 |
| identity link 管理 | `/api/v1/identity/sso/links` と ID route。専用 permission、actor / tenant を監査 |

匿名の start / exchange / launch に framework の `.WithIdempotency()` を流用しない。独自 transaction / protocol 一回性を使う。公開値も tenant 列挙と abuse を考慮して rate limit し、秘密・内部接続情報は返さない。

| 場所 | 初期変更 |
| --- | --- |
| `src\Modules\Identity\Modules.Identity.Contracts\v1\Sso\{Feature}\` | command / query / response |
| `Modules.Identity.Contracts\DTOs\` / `Authorization\` | secret を含めない DTO、SSO 管理 permission |
| `Modules.Identity\Features\v1\Sso\{Feature}\` | handler / validator / endpoint |
| `Modules.Identity\Domain\` / `Data\Configurations\` | 共通接続・OIDC 設定・mapping・session 属性 |
| `Modules.Identity\Authentication\Sso\Oidc\` | handler events、scheme / options / metadata、callback adapter |
| `Modules.Identity\Authentication\Sso\` / `Services\` | transaction / handoff、identity mapping、共通ログイン完了 |
| `Modules.Identity\Localization\` / `IdentityModule.cs` | resource keys、DI、options、endpoint 登録 |
| `src\Host\FSH.Starter.Api\Program.cs` / 設定 | bootstrap、`SsoOptions`、public base URI、shared key ring |
| `src\Directory.Packages.props` / Identity csproj | OIDC の必要依存のみ。SAML package は追加しない |
| `src\Host\FSH.Starter.Migrations.PostgreSQL\Identity\` | additive migration / snapshot |
| `clients\dashboard\src\api\sso.ts` / `src\pages\auth\` | login / callback / MFA、AuthProvider 接続 |
| `clients\admin\src\api\sso.ts` / `src\pages\` | operator 接続管理・link 管理 |
| 既存 test projects / 各 app の `tests` | unit / integration / architecture / browser |

`SsoOptions` は Enabled、public base URI、app ごとの callback、TTL・通信制限を持ち、有効時に fail-fast validation する。secret と tenant IdP 設定を SPA `config.json` に置かない。DbMigrator には外部 IdP 通信やブラウザー用設定を必須にしない。

secret は secret manager 参照を基本とし、代替の暗号化保存は共有 key ring と復旧手順を検証してから採用する。設定 API は write-only 更新、取得は `hasSecret` 等のみ。secret や protocol payload は HTTP audit / request logging / exception / traces からも除外する。接続テストは secret の存在や Discovery 取得だけで成功にせず、現 configuration version の実認証 callback まで通過したことを有効化判断に使う。

operator は root の認可された target-tenant 操作として管理する。一般ユーザーの任意 tenant header で設定を書き換えられないことを試験する。admin の permission catalog / RouteGuard を同期し、dashboard に RHF / zod / admin 用 RouteGuard を持ち込まない。tenant 管理者の self-service は後続とする。

## 8. 作業順序とリリースゲート

| 段階 | 実施内容 | 完了条件 |
| --- | --- | --- |
| P0: OIDC PoC / ADR | Entra client 所有者、dynamic scheme、callback 前 tenant、shared / dedicated DB、secret / distributed store、cookie / proxy を確定 | tenant A の開始を別 instance が処理でき、tenant B への差替えを拒否。SAML PoC は不要 |
| P1: 永続化・管理 | connection / OIDC 設定 / mapping / session provenance、migration、CRUD / link / permissions / audit | tenant isolation、unique 制約、secret 非露出、並行更新、DbMigrator 適用を確認 |
| P2: OIDC 縦断 | start / launch / callback / handoff / MFA / JWT / refresh、dashboard UI | 正常ログインと protocol / tenant / app / code 再送の拒否を自動化 |
| P3: 運用・先行公開 | operator UI、Entra 実接続、rotation、logout、障害・回復、公開 docs | OIDC のみで利用可能。SAML package / 接続検証 / UI を待たない |
| P4: OIDC 運用拡張 | Google / Okta OIDC、tenant self-service、必要に応じ JIT / Required / SCIM の別設計 | 顧客需要で採否を判断。全項目を SAML の前提にしない |
| S1: 将来の SAML | SAML adapter / 設定 / ACS / metadata / 証明書 / protocol tests | 共通ログイン完了を使い、既存 OIDC を壊さず追加リリース |

P1 は P0、P2 は P1、P3 は P2 の完了を前提にする。S1 は P3 後の独立作業で、P4 全体には依存しない。実際の SAML 顧客要求が早ければ、P4 の機能拡張より S1 を優先できる。

初期 UI は既存ユーザー登録・明示的 mapping と設定の作成 → 診断 → 実認証検証 → 有効化までを含める。PoC の手書き SQL / appsettings 編集だけで顧客導入を完了扱いにしない。実認証テストは login とは purpose を分け、テスト実行だけで一般ユーザーの session を発行しない。

段階公開は additive migration → feature flag off 配備 → 検証 tenant の Optional OIDC → Entra 試験・回復試験 → 顧客へ拡大とする。rollback は OIDC 接続無効化と flag によって行い、mapping を削除しない。Required は初期未提供なので、password ログイン可能性を運用説明で明示する。

工数・納期は P0 後に見積もる。動的認証 scheme と callback tenant 解決、既存 MFA / refresh との整合が主要な不確実性であり、SAML を後送りしても省略できない。

## 9. OIDC リリースの検証項目

| 分類 | 必須試験 |
| --- | --- |
| protocol | 署名・issuer・audience・期限・nonce・state・PKCE の不一致、PAR、userinfo sub、IdP cancellation、鍵 rotation |
| tenant / app | tenant / connection 差替え、root + dashboard、既存 Bearer / header の混入、shared DB / dedicated DB |
| identity / MFA | 未連携、同じ email で異なる sub、停止 user、期限切れ tenant、直接 role + group role、ローカル TOTP |
| 一回性 | 並行 launch / callback / exchange / MFA、code 期限切れ、同時タブ、戻る・再読込、途中失敗後の再送 |
| distributed | A 開始 → B callback → C exchange、Redis 障害、再起動、key ring rotation、旧新 config version |
| 管理 | 他 tenant の設定取得・更新拒否、secret 非露出、SSRF、権限、設定競合、接続停止と refresh |
| browser | 実 IdP redirect、cookie / CORS、proxy、履歴への token 非露出、permissions hydration、realtime |
| regression | password / refresh / impersonation / locale / root operator、flag off、単一 refresh token の既存挙動 |
| architecture | Contracts / runtime 分離、validator の型対応、Module 境界、IdentityDbContext の tenant isolation |

UI の route-mocked Playwright だけでは OIDC cookie / redirect / signature の証明にならない。既存 Testcontainers と controlled OIDC fixture / Keycloak、real-browser E2E を組み合わせる。Entra は別の検証環境で実接続し、CI は顧客 IdP の可用性に依存させない。

既存 `BaseDbContext` 向け isolation test だけで新 Identity entity が検証済みとは扱わない。IdentityDbContext の model と実際の cross-tenant query を確認する。動的 scheme の state 保護目的の違いと callback 取り違えも明示的に試験する。

SSO の新規 code には focused unit / handler / validator tests を追加し、既存 auth regression を実行する。backend は対象 test projects と Api / DbMigrator build、frontend は各 app の既存 build / lint / Playwright scripts を使う。テスト selector の 0 件実行を成功と扱わない。承認済み BuildingBlocks 変更を行う場合は full suite 要件にも従う。

SAML の実 protocol test は初期リリースの gate にしない。一方、protocol-neutral な完了処理の unit test は OIDC library の型なしで実行できるようにし、将来の adapter 追加で再利用する。

## 10. 将来 SAML を追加するときの差分

### 追加するもの

1. 採用時点の .NET 対応・license・advisory を確認した SAML library と runtime adapter。
2. 共通 `SsoConnection` に関連する SAML 固有設定。IdP entity ID、SSO URL、SP entity ID、ACS、識別子方針、署名・暗号化設定。
3. AuthnRequest、RelayState / InResponseTo、SAML ACS、SP metadata、IdP verification certificate と SP signing / decryption key の管理。
4. persistent NameID / 不変属性の mapping。NameID format / qualifiers を含む identity key 表現の追加。
5. XML 署名・audience・Recipient・Destination・時刻・replay・XML wrapping 等の専用検証と cross-site POST の browser 試験。
6. Entra SAML と controlled SAML IdP による相互接続検証、管理 UI・docs の追加。

### 維持するもの・変えないもの

共通 connection ID、tenant 境界、明示的 user link、eligibility / MFA / 内部 claim 構築、JWT / refresh 契約、handoff、session / audit の基本契約を維持する。protocol 固有の検証は adapter 内に閉じ、OIDC callback を SAML parser に通さない。

SAML の外部 identity を同じメールの OIDC identity と自動統合しない。同じ内部ユーザーへ連携する場合も明示的に別 mapping を登録する。認証方式の切替で既存 OIDC mapping や接続を上書きせず、検証済み新接続への切替と旧接続の失効を管理する。

SAML のために必要な schema は additive migration で追加し、既存 OIDC data / callback URI / secret 参照を維持する。SAML 用 SameSite=None は必要な一時 cookie に限定し、OIDC の既存 cookie 保証を弱めない。distributed transaction store は共通化しても、nonce と SAML replay を同じ意味のフィールドとして流用しない。

初回 SAML は SP-Initiated に限定する。unsolicited IdP-Initiated と SLO は別設計とする。SAML 対応時の回帰 gate に OIDC 正常系・tenant 分離・refresh・MFA・secret rotation を含める。

## 11. 後続の運用機能と未決事項

JIT、SSORequired、SCIM、IdP logout は OIDC の成熟度向上として独立して検討でき、SAML 実装と一括導入しない。

- JIT: tenant opt-in、最小権限、Users quota、同時作成とメール衝突、停止 user の扱いを設計する。
- Required: password issue / self-registration / 既存 local refresh / SSO refresh をサーバーで制御し、認可された break-glass と発行済み JWT の失効 SLA を先に決める。
- SCIM: 認証から分離した tenant-scoped provisioning credential、Users / Groups / PATCH / deactivation、session 失効を別計画にする。
- IdP logout: アプリ logout と IdP session 終了を分け、対応 IdP と failure semantics を明示する。

P0 で確定する事項は、Entra registration の所有者、対応 deployment topology、動的 scheme 選択、secret manager、共有 key ring / 原子的 store、MFA continuation、接続無効化後の失効時間、operator onboarding 手順とする。SAML library 選定・証明書仕様は将来 S1 の決定事項とし、P0 の blocker にしない。

監査・ログ・trace に secret、token、認可 code、launch / handoff / continuation の生値を出さない。成功率・latency・理由別失敗・IdP 通信・key rotation を観測し、user / subject を metric label に入れない。

リリース時には別リポジトリ `fullstackhero/docs` の OIDC 利用ガイド・Entra 接続手順・Optional の制約・logout / 失効・障害回復と changelog を更新する。SAML、JIT、Required、SCIM は未提供として記載する。本作業はローカル計画文書の追加のみであり、外部 docs repo の更新や実装は含まない。

## 12. 参照先

- リポジトリの既存両方式計画: `docs\sso-oidc-saml-implementation-plan.md`
- 実装規約: `AGENTS.md`、`.agents\rules\`、`.agents\skills\`
- [ASP.NET Core: OIDC Web authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)
- [OpenID Connect Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html)
- [Microsoft: SaaS ISV の SSO 計画](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/plan-sso-integration-isv)
- [Google: OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect)
- [OASIS: SAML 2.0 specifications](https://docs.oasis-open.org/security/saml/v2.0/)

参照先の package 対応・推奨設定は実装着手時に再確認し、公式文書の記載だけで実際の接続試験を代替しない。
