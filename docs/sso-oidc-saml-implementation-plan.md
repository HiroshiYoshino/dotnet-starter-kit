# マルチテナント B2B SaaS の OIDC / SAML 2.0 SSO 実装計画

作成日: 2026-10-14

ステータス: 実装前の計画。記載した新規型、API、設定、ファイルは提案であり、既存機能ではない。

## 1. 目的・優先順位・前提

顧客企業ごとの IdP と連携し、テナントをまたがない企業 SSO を提供する。製品として OIDC と SAML 2.0 を同じ最優先に位置付け、実装順は共通基盤 → OIDC → SAML とする。SAML が導入条件となる顧客には、SAML 完了前の Phase 1 を「SSO 対応済み」と説明しない。

| 観点 | 方針 |
| --- | --- |
| 最優先プロトコル | OIDC と SAML 2.0 |
| 初期接続検証 | Microsoft Entra ID の OIDC / SAML |
| 次の接続検証 | Google Workspace、Okta。Keycloak は自動試験用 IdP として初期から利用 |
| 製品固有対応 | 標準プロトコルを実装し、Entra の組織識別や Google の組織制限は明示的な接続プロファイルで補う |
| 初期アプリ | tenant-facing の `clients\dashboard`。root operator の `clients\admin` ログインは従来方式を維持 |
| 初期接続数 | 1 テナントにつき有効な接続は 1 件。データモデルは複数接続を識別できる形にする |
| 顧客属性 | 一般企業向けを仮定。官公庁・教育・閉域網・認証強度の固有要件は Phase 0 で確認する |
| 対象外 | Phase 1 の SCIM、IdP-Initiated SSO、SLO、LDAP 直接連携、WS-Federation、CAS |

OIDC / SAML は認証、アプリ内の権限は既存 Identity の roles / groups / permissions によって管理する。JIT と SCIM はユーザーのライフサイクル管理であり、認証成功とは分離する。

## 2. リポジトリで確認した接続点

以下は計画作成時に確認した実装である。実装着手時に再確認する。

| 既存ファイル・領域 | 確認した内容と計画への影響 |
| --- | --- |
| `src\Modules\Identity\Modules.Identity\IdentityModule.cs` | ASP.NET Identity、JWT Bearer、ユーザーサービス、sessions を登録している。SSO の基本的な所属先は Identity とする |
| `Authorization\Jwt\JwtAuthenticationExtensions.cs` | default authenticate / challenge は Bearer。SSO 導入後も維持し、API の 401 を IdP リダイレクトへ変更しない |
| `Features\v1\Tokens\TokenGeneration\GenerateTokenEndpoint.cs` | `tenant` と `X-FSH-App` を受け取り、root と dashboard の組み合わせを拒否する。SSO でもサーバー側で app / tenant 境界を維持する |
| `Features\v1\Tokens\TokenGeneration\GenerateTokenCommandHandler.cs` | 資格情報検証後、JWT 発行、refresh token 保存、session 作成、監査、integration event 発行を行う |
| `Features\v1\Tokens\RefreshToken\RefreshTokenCommandHandler.cs` | refresh token と session を検証し、token rotation を行う。SSO も既存 refresh 経路と接続する |
| `Services\IdentityService.cs` | ユーザー有効性、メール確認、テナント有効性・契約猶予、ローカル 2FA、直接 role と group 由来 role を扱う |
| `Services\IdentityService.cs` の `BuildClaimsForUserAsync` | impersonation 用の経路で、通常ログインと同一の検証・group role 構築ではない。SSO の共通完了処理としてそのまま使わない |
| `Services\UserRegistrationService.cs` | `GetOrCreateFromPrincipalAsync` は既存の補助処理。ただしメール一致で既存ユーザーを返し、外部識別子による連携確認を行わない。新 SSO から直接呼ばない |
| `Domain\FshUser.cs` | `ObjectId` はあるが、テナント別・複数プロトコル・複数接続の外部 identity を表現するには不足する |
| `Data\IdentityDbContext.cs` / `Data\IdentityConfigurations.cs` | Finbuckle の Identity DbContext。`UserLogins` も multitenant。新規 SSO entity にも tenant isolation を適用する |
| `src\Modules\Multitenancy\Modules.Multitenancy\MultitenancyModule.cs` | Claim → Header → query のテナント解決があり、root override は認証後。SSO callback のテナントを通常の header/query 任せにしない |
| `src\Host\FSH.Starter.Api\Program.cs` | `UseHeroMultiTenantDatabases()` の後に `UseHeroPlatform()` を呼ぶ |
| `src\BuildingBlocks\Web\Extensions.cs` | `UseAuthentication()` は Module middleware より前。OIDC の remote callback が Module middleware より先に処理される点が重要 |
| `clients\dashboard\src\auth\auth-context.tsx` / `token-store.ts` | アプリ内 JWT と refresh token を保持し、permissions を別途取得する。SSO も同じ認証状態更新に接続する |
| `clients\dashboard\src\auth\api.ts` | `issueToken` は `X-FSH-App: dashboard` を送信する |

`TokenResponse` の契約は `AccessToken / RefreshToken / RefreshTokenExpiresAt / AccessTokenExpiresAt`。refresh 応答は別契約の `Token / RefreshToken / RefreshTokenExpiryTime` である。新しい交換 API は初回ログインの `TokenResponse` を返し、既存 refresh の契約を変更しない。

## 3. AI 向け指示との整合

実装前に `AGENTS.md` と、`.agents\rules` の `architecture.md`、`api-conventions.md`、`database.md`、`security.md`、`modules\identity.md`、`modules\multitenancy.md`、`resilience.md`、`localization.md`、`testing.md`、`integration-testing.md`、frontend の各ルールを読む。

| 制約 | 実装への適用 |
| --- | --- |
| Module 境界 | Identity から Multitenancy / Auditing へは Contracts または既存 framework interface のみでアクセスする |
| Vertical Slice | request / response は Identity.Contracts、handler / validator / endpoint は runtime の同一 Feature に配置 |
| Mediator | MediatR ではなく Mediator。handler は `public sealed`、`ValueTask<T>`、await は `.ConfigureAwait(false)` |
| Validator | 全 command と paginated query に validator を用意。callback の protocol validation は別途ライブラリで行う |
| テナント分離 | 新 entity は tenant-isolated。匿名 endpoint であることを `IGlobalEntity` の理由にしない |
| DbContext | Identity は `BaseDbContext` ではない。既存の `base.OnModelCreating` → configurations → `ApplyTenantIsolationByDefault()` を維持する |
| Migration | 中央 PostgreSQL Migrations の Identity フォルダーへ追加し、DbMigrator で適用。API 起動時に migrate しない |
| BuildingBlocks 保護 | 無承認で `src\BuildingBlocks` を変更しない。Module / Host の拡張を優先する |
| エラー・監査 | ProblemDetails、resource key による localization、構造化ログ、CancellationToken、TimeProvider を使用 |
| Frontend | `apiFetch`、runtime `config.json`、named export / lazy route、`mutate(arg)` を維持 |
| ドキュメント | リリース時は別リポジトリ `fullstackhero/docs` の利用・運用ドキュメントと changelog も更新する |

実装には `add-feature`、`add-entity`、`create-migration`、`add-permission`、`add-react-page` / `add-full-slice`、`testing-guide`、`mediator-reference` を作業内容に応じて利用する。本計画では新 Module を増やさないため、既存の Module 登録 4 か所に新しい Module を足す必要はない。将来独立 Module に分割する場合は Api / DbMigrator の Mediator assembly 指定と module assembly 指定の計 4 か所を更新する。

## 4. 推奨アーキテクチャ

### 4.1 Identity を拡張し、API の信頼境界を維持する

Identity 内に SSO 接続管理、OIDC RP、SAML SP、外部 identity 連携、ログイン完了処理を追加する。外部 IdP token / assertion は SSO callback だけで検証し、通常 API が受け付ける Bearer token は引き続きアプリ自身が発行する JWT に限定する。

SPA が IdP token を検証したり、client secret を保持したり、SAML assertion を解析したりする構成にはしない。OIDC はバックエンドの confidential client + Authorization Code + PKCE、SAML はバックエンドの SP とする。

初期導入で全アプリを cookie / BFF に移行することは対象外とする。既存 localStorage token 保存の XSS リスクは解消されないため、SSO がその対策になったとは説明しない。BFF 移行は別 ADR / 変更計画で扱う。

### 4.2 テナントを先に確定し、対象 DB を選ぶ

Phase 1 の HRD は、明示的な tenant identifier と接続の公開 ID に基づく。メールアドレスから全テナントの Users を検索しない。

SSO 接続設定と外部 identity mapping は `IdentityDbContext` に保持し、既存の shared DB / per-tenant DB の両方に従う。認証開始時に既存 `IMultiTenantStore<AppTenantInfo>` で tenant を解決し、そのテナントを設定した scope 内で IdentityDbContext を生成して接続を取得する。

callback は custom header を送信できない。URL の tenant / connection は設定を探すための候補にすぎず、認証されたテナントではない。最終的な tenant は、サーバーで発行した保護済み state / RelayState と、サーバー側 login transaction の一致によって確定する。

OIDC handler は `UseAuthentication()` 内で callback を処理できるため、Identity の `ConfigureMiddleware` だけに tenant 初期化を置いてはいけない。Host に接続する Module-owned の限定 bootstrap middleware を PoC する。`UseHeroMultiTenantDatabases()` 後、`UseHeroPlatform()` 前で、SSO callback の候補解決と必要な scheme の準備のみを行い、token 発行・redirect・成功応答は行わない。自身の異常は安全に拒否・記録し、後段 ExceptionHandler に依存して握りつぶさない。

その位置では platform の forwarded headers / security headers / rate limiting より前になる。callback URL は固定した public base URI から生成し、request Host / scheme に依存しない。候補解決の高コスト処理には個別の回数・時間制限を設ける。remote handler が後段 middleware を short-circuit する callback は、protocol event / endpoint 内でも監査・拒否応答・必要な headers・リクエスト制限を保証する。

bootstrap で生成した scoped DbContext / UserManager を singleton scheme provider や options cache に保持しない。tenant context は同じ async 実行経路で設定し、callback における既存 Bearer tenant / root override と競合した場合も transaction の tenant 以外へ切り替えない。

### 4.3 動的接続・options のライフサイクル

ASP.NET Core OIDC handler を利用し、tenant / connection / configuration version 単位の named scheme と immutable options を提供する。共有 `OpenIdConnectOptions` の Authority / ClientId / secret をリクエストごとに上書きしない。

named scheme の追加・ロード・削除、callback routing、`IOptionsMonitorCache` の無効化、metadata cache の分離を Phase 0 の PoC で確定する。メモリに全接続を無制限登録する方式や、ユーザー入力を scheme 名として無検証で登録する方式は採用しない。

設定変更時は version を更新して全 API instance の cache を失効させる。進行中 transaction は短い TTL の間だけ対応する設定 snapshot を参照できるようにし、接続無効化・秘密漏えい等の強制失効は snapshot も拒否する。callback URI / SP entity ID は configuration version ごとに変えない。

SAML の protocol configuration も接続ごとの snapshot とし、証明書・issuer・audience の混在を防ぐ。

OIDC の成功イベントでは remote handler の通常の cookie sign-in をそのままアプリ認証に使わず、検証済み identity から handoff 発行へ明示的に完了させる。ライブラリ上 SignInScheme が必要なら SSO 専用の短期 scheme を指定し、既存 ASP.NET Identity application cookie に外部 principal を保存しない。API の default authenticate / challenge と認可は Bearer のままとする。

Module-owned `SsoOptions` には Enabled、固定 public base URI、app 別 frontend callback、transaction TTL、metadata / payload 制限を持たせ、Options pattern と有効時の fail-fast validation を使う。tenant ごとの IdP 設定を appsettings の static scheme 一覧や SPA の config.json に格納しない。DbMigrator には外部 IdP 接続・ブラウザー認証用設定を必須にしない。

### 4.4 API から SPA へのログイン引き渡し

access token / refresh token / ID token / assertion を redirect URL の query、fragment、ログに載せない。callback 後は短寿命・一回限りの opaque handoff code のみを登録済み SPA callback に渡す。

1. SPA は暗号学的乱数の verifier を生成し、タブ単位の sessionStorage に短期保存する。SHA-256 challenge を開始 API に渡す。
2. 開始 API は tenant、connection、app、許可済み return path、challenge、接続 version を保存する。API origin の一時 browser binding cookie も発行する。
3. OIDC state / nonce / PKCE、または SAML AuthnRequest / RelayState を作成し、IdP へ遷移する。
4. callback は protocol validation と login transaction / browser binding を検証する。外部 identity を確定し、短寿命の handoff code を発行する。この時点で最終 JWT は SPA に返さない。
5. SPA は handoff code、verifier、app を交換 API に POST する。API は browser binding、challenge、一回性、TTL、tenant、接続の現状を検証し、必要な MFA を完了してから JWT を発行する。
6. `AuthProvider` の共通 session 設定処理に接続し、permissions hydration と realtime 再接続を既存同様に行う。

保護済み state の内部には tenant を含められるが、外部に渡す RelayState / handoff code は高エントロピーの参照値を基本とする。code は hash で保存し、消費は Redis または DB の compare-and-set / conditional update によって原子的に行う。`IDistributedCache.Get` → `Remove` だけでは一回性を保証できない。

Redis 障害時に instance-local store へ黙って fallback しない。SSO は fail closed とし、SSO 無効テナントの既存ローカルログインは維持する。transaction の状態を Pending / Verified / Consumed / Failed とし、検証成功・code 消費・token 発行の並行実行で二重発行しない。途中失敗した code を復活させず、ユーザーに認証の再開始を案内する。

暫定 TTL は login transaction 5 分、handoff code 60 秒。実測と顧客の MFA 所要時間に応じて Phase 0 で確定し、TimeProvider による境界テストを行う。

一時 cookie は HttpOnly / Secure、用途・接続の scope を限定する。SAML の cross-site POST を考慮して必要な cookie にのみ SameSite=None を採用する。OIDC correlation / nonce cookie と独自 binding cookie の寿命・path をそろえて試験する。SPA の開始・交換 API は必要な呼び出しだけ `credentials: include` を使い、許可済み Origin を厳格に検証する。cross-site cookie を拒否するブラウザーでも成立するよう、初期推奨配置は同一 site の API / dashboard または reverse proxy 配下とし、別 site 配置はブラウザー実測後にサポート可否を明記する。

## 5. データモデル・外部 identity・秘密情報

| 提案 entity / store | 所属・主要項目・制約 |
| --- | --- |
| `SsoConnection` | Identity / tenant-isolated。Id、Protocol、DisplayName、Enabled、ConfigurationVersion、検証状態、OIDC issuer/client ID または SAML entity ID / endpoint / 識別子方針、秘密参照、証明書、更新者・更新日時 |
| `ExternalIdentityLink` | Identity / tenant-isolated。UserId、ConnectionId、Protocol、Issuer、Subject、SAML NameID format / qualifiers、連携元・連携日時 |
| `TenantSsoPolicy` | Identity / tenant-isolated。Optional / Required、JIT 許可、MFA 方針、break-glass 方針。Phase 1 は Optional のみ |
| `SsoLoginTransaction` | Module-owned distributed store。tenant、connection、version、app、return path、state、browser binding、challenge、AuthnRequest ID、期限、状態。private transient data として保持 |
| `UserSession` の追加属性 | 認証方式、ConnectionId、認証時刻、認証強度判定、policy version。SSORequired や refresh 再評価はサーバー保存の出自を基準にする |

外部 identity の照合キーは `(TenantId, ConnectionId, Protocol, Issuer, SubjectKey)` とする。OIDC の SubjectKey は検証済み `sub`、SAML は設定した不変属性または persistent NameID と format / qualifiers の組み合わせ。大文字小文字の変更や URL 正規化によって異なる subject / issuer を同一視しない。transient NameID やメールアドレスを永続キーとして扱わない。

外部 identity key の unique index と tenant に属する User / Connection の整合性を DB と handler の双方で保証する。識別子に長さ上限を設け、hash key を index に用いる場合は元値の完全一致も検証する。OIDC の pairwise subject、ClientId 変更、接続再作成、SAML entity ID / NameID 変更は別 identity として扱い、明示的な再連携を必要とする。

既存 `IdentityUserLogin` の利用も Phase 0 で比較する。ただし標準構造だけでは issuer / subject / connection / tenant と管理監査を十分に表現できないため、本計画の基本案は専用 `ExternalIdentityLink` とする。二つの表に同じ mapping を二重管理しない。

### 5.1 既存ユーザー連携と JIT

Phase 1 は事前連携された既存ユーザーのみログインを許可する。管理者による明示的な mapping 登録、またはローカル認証・必要な MFA・直近再認証済みの本人による link transaction を用意する。本人 link は開始時の user / tenant を固定し、callback の別アカウントや別 tenant に差し替えできないようにする。unlink で最後のログイン手段を失う操作も防止する。

メール一致、OIDC の `email_verified`、Google の `hd`、SAML のメール属性だけで既存ユーザーに自動連携しない。未連携 identity は安全なエラーにし、存在するユーザーを匿名応答で列挙しない。

Phase 2 の JIT は tenant ごとの opt-in。ロールは最小権限、既存アカウントとのメール衝突は自動統合せず管理者確認とする。Users quota、ユーザー作成、mapping、role / default group、必要なイベントの整合性を保証し、同時 callback で二重作成しない。停止済みユーザーを JIT で再有効化しない。

メール確認状態は IdP の属性を一律に信頼せず、接続ごとの検証済み契約に基づく。SAML に `email_verified` と同等の標準保証があると仮定しない。

### 5.2 秘密・証明書・metadata

- client secret と SP private key は secret manager の参照を基本とする。限定環境では共有・永続化した Data Protection key ring による暗号化保存を評価する。保存方法を実装前に確定する。
- secret 更新 API は write-only。取得 DTO、OpenAPI の example、監査 diff、ログ、UI へ実値を返さない。private key と passphrase も同様に扱う。
- metadata / discovery / JWKS / token / userinfo の取得先は HTTPS と allowlist を基本とし、localhost、private / link-local / metadata service IP、DNS rebinding、外部 redirect による SSRF を防ぐ。閉域 IdP は専用ネットワークポリシーによる明示的例外とする。
- IdP signing key を OIDC Discovery / JWKS 経由で更新する。unknown kid の再取得には回数制限を設け、任意 issuer の鍵を採用しない。
- SAML signing certificate は旧新 overlap を管理し、失効日を通知する。metadata 内の任意 certificate を無条件に信頼しない。手動 pinning または事前に信頼した署名付き metadata によって更新する。
- SP signing / decryption key と IdP verification certificate を区別する。SP key 更新は metadata 公開、IdP 設定反映、旧 key 廃止の手順を持つ。
- 複数 API instance で Data Protection key ring、transaction store、replay cache と configuration invalidation を共有する。永続 key の復元・rotation 後も有効期限内 callback を処理できることを確認する。

## 6. プロトコル別の実装方針

### 6.1 OIDC

第一候補は `Microsoft.AspNetCore.Authentication.OpenIdConnect`。中央 `src\Directory.Packages.props` と Identity runtime の project で依存を管理し、既存 Microsoft.IdentityModel 依存との互換性を確認する。

Authorization Code Flow、PKCE S256、state、nonce、confidential client 認証を使用する。implicit flow と password grant は実装しない。基本 scope は `openid profile email`、IdP の refresh token / downstream API scope は必要がないため要求しない。アプリの refresh token と IdP の refresh token を混同しない。

issuer、audience、署名、期限、nonce、必要な `azp`、userinfo を利用する場合の `sub` 一致を検証する。外部 claim をそのままアプリの role / tenant / SuperAdmin claim にコピーしない。`SaveTokens` による IdP token の恒久 cookie 保存は不要とする。

Entra の初期接続は顧客 directory に固定した authority / issuer とする。`common` の token を tenant 制約なしに受け付けない。将来の SaaS 共有 multitenant app registration は、issuer と許可された Entra tenant ID の対応、consent / onboarding を別途設計する。

Google は Google Cloud の OIDC client と Workspace の SAML enterprise app を別の接続手順として説明する。OIDC の `hd` request parameter は認可条件ではない。組織限定を要求する場合は検証済み ID token の `hd` と接続に登録した組織制限を確認し、許可されない consumer account を拒否する。

.NET の OIDC handler が Discovery に応じて PAR を使用することもあるため、IdP ごとの対応差を試験する。metadata / JWKS の GET には既存 resilience helper を評価し、認可 code の token POST や PAR POST に汎用自動 retry を無条件適用しない。

### 6.2 SAML 2.0

XML 署名・canonicalization・binding・暗号化の処理を自作しない。第一候補は `ITfoxtec.Identity.Saml2` の core package とし、Minimal API adapter を Identity runtime に置く。公式 README は .NET 10 と BSD-3-Clause を記載しているが、採用するリリース artifact の TFM / 依存 / license / 既知 advisory を PoC 時に確認して version を固定する。

`Sustainsys.Saml2` は比較候補。公式 README は v3 開発と v1 / v2 の限定保守を説明しているため、未確認の .NET 10 対応や開発 branch を本番採用の根拠にしない。商用ライブラリまたは外部 federation broker は、保守・費用・運用責任の比較対象に留め、初期要件には追加しない。

Phase 1 は SP-Initiated、AuthnRequest の HTTP-Redirect、ACS の HTTP-POST を基本とする。SP metadata と接続単位の entity ID / ACS URI を提供する。顧客要件に応じて AuthnRequest 署名と assertion encryption を設定可能にする。

検証対象は IdP issuer、SP audience、Destination、Recipient、InResponseTo、SubjectConfirmation、NotBefore / NotOnOrAfter、署名の trust / algorithm、response / assertion ID の replay。受理する assertion は一つに限定し、署名で保護された assertion と claims 抽出対象が同一であることを保証する。SHA-1、DTD / external entity、署名なし assertion、XML signature wrapping を拒否する。

Response 全体の署名または Assertion 署名の受理条件を接続ごとに定義し、未署名 envelope のフィールドを信頼しない。サイズ・XML depth・assertion 数・展開サイズ・処理時間に上限を設ける。replay は全 instance で原子的に予約し、期限切れ assertion を時刻許容幅だけで復活させない。

Phase 1 は unsolicited response / IdP-Initiated を拒否する。Phase 2 で opt-in にする場合は、開始 transaction がないため同じ CSRF 保証が得られないことを明記し、別の landing / browser confirmation / 接続固定 / app 固定 / replay 防御を設計してから公開する。

## 7. 共通ログイン完了・ポリシー・運用

### 7.1 既存ログインと共通化する範囲

password validation と external proof validation は別にし、その後の account eligibility / claim construction / token finalization を Identity 内で共有する。

既存 service / handler から、必要な範囲だけ `SignInEligibilityService`、`ApplicationClaimsFactory`、`SignInCompletionService` 相当へ抽出する。名称は実装時に既存命名へ合わせる。局所的な抽出とし、UserService facade や impersonation の意味を無関係に変えない。

SSO では次を必須にする。

- transaction / mapping の tenant、ユーザーの tenant、選択 DB の tenant が一致する。
- ユーザーが有効、接続が有効、テナントが有効で契約猶予内。停止・設定失効は code 交換時にも再確認する。
- claim はアプリ内のユーザー・直接 role・group role・locale から生成する。IdP の admin / role / tenant 値で昇格しない。
- root operator と dashboard の境界を、callback 時の header ではなく開始 transaction の app / tenant と登録した frontend origin に基づいて検証する。Phase 1 は root の SSO 開始自体を対象外とする。
- ローカル `TwoFactorEnabled` を黙って bypass しない。初期は短期 MFA continuation と既存 TOTP を要求し、IdP MFA の代替認定は管理者承認済み `acr / amr / AuthnContext` の対応表がある場合のみ許可する。
- refresh token の hash 保存、session 作成、ログイン / token 監査、既存 token event を共通経路で実行する。イベントは既存 Outbox を利用し、Module 固有 Outbox を追加しない。

既存ローカルログインの session 作成は best-effort である。SSO は認証出自と再評価に依存するため session 作成失敗時は token を返さない。共通化しても従来ローカルログインの動作を無断で strict に変更しない。

現状 refresh token は FshUser に一つ保存される。SSO 導入で全 device 独立 refresh token 管理へ変更しない。ただし複数ログイン時の既存 rotation / session の挙動を受け入れテストで明示する。

### 7.2 SSORequired、退職・失効、ログアウト

Phase 2 の SSORequired は UI だけではなく、password token issue、self-registration、既存 local refresh、SSO refresh の全経路でサーバー enforcement する。サーバー保存した session provenance に接続と policy version を持ち、未検証 access token の claims を根拠に許可しない。

SSORequired への変更時は事前に接続を実ログインで検証し、ローカル session / refresh を失効させる。発行済み JWT は既存 Bearer 検証だけでは即時失効しないため、短い token TTL と request-time policy / session 判定の要否・許容失効時間を決定する。即時失効を製品保証するなら必要な middleware / validation を完成してから有効化する。

break-glass は権限分離、MFA、用途制限、事前指定アカウント、利用監査を必須とする。root operator の回復手段とは区別し、単なる任意ユーザーの password fallback にしない。IdP 障害で Required が Optional に自動変更される実装は禁止する。

IdP でユーザーを無効化してもアプリ内 JWT / refresh token が自動失効するとは限らない。Phase 1 はローカル無効化と期限・refresh 再評価を運用手順にする。SCIM 完了後は deactivation → refresh / session revoke と access token の失効時間保証に接続する。

Phase 1 の logout はアプリの session / refresh を失効させる API と browser token 消去まで。IdP session は残ることを明示する。OIDC RP-Initiated Logout、front/back-channel logout、SAML SLO は別の Phase 2 作業とし、全 IdP の同時 logout を保証しない。

## 8. 提案 API とファイル配置

通常 API は Identity の version group 配下とする。以下のルートは案であり、OpenAPI と既存 route の衝突を確認して確定する。

| 操作 | 提案ルート / 認可 |
| --- | --- |
| HRD | `POST /api/v1/identity/sso/discover`。匿名、tenant identifier を入力。公開 label / protocol / connection public ID のみ返す |
| ログイン開始 | `POST /api/v1/identity/sso/start`。匿名、tenant / app / connection / challenge / return path を検証 |
| SPA code 交換 | `POST /api/v1/identity/sso/exchange`。匿名、verifier / binding / transaction の検証。成功時 `TokenResponse`、MFA 必要時は明示的な continuation 契約 |
| MFA 完了 | `POST /api/v1/identity/sso/complete-mfa`。匿名だが短期 continuation proof 必須。成功時 `TokenResponse` |
| 接続一覧 / 作成 / 更新 / 無効化 | `GET / POST / PUT / DELETE /api/v1/identity/sso/connections` と ID route。専用 permission |
| 接続診断・検証開始 | `POST /api/v1/identity/sso/connections/{id}/test`。専用 permission、SSRF 制約、秘密を含まない結果 |
| 外部 identity 連携 / 解除 | `POST / DELETE /api/v1/identity/sso/links`。管理 permission または直近再認証済み本人の限定操作 |
| SSO policy | `GET / PUT /api/v1/identity/sso/policy`。専用 permission。Required は Phase 2 から |
| OIDC callback | `/auth/sso/{tenantPublicId}/{connectionPublicId}/oidc/callback`。remote handler の固定 CallbackPath に対応 |
| SAML ACS / metadata | `/auth/sso/{tenantPublicId}/{connectionPublicId}/saml/acs` と `/metadata`。固定 SP URL |

protocol callback / metadata は IdP に登録する安定 URI とし、API version の変更で破壊しない。OIDC callback は通常の Mediator endpoint として二重に map しない。remote handler の検証イベントから内部サービスへ渡す。SAML ACS の adapter は runtime 内に置き、検証後の結果だけ共通完了処理に渡す。

匿名 start / exchange / ACS に `.WithIdempotency()` を付けない。SSO 専用の一回性・replay 検証を使う。通常の管理 POST は適用可能性を個別判断する。

| 追加・変更場所 | 内容 |
| --- | --- |
| `src\Modules\Identity\Modules.Identity.Contracts\v1\Sso\{Feature}\` | request / response。例: `CreateSsoConnectionCommand`、`GetSsoConnectionsQuery`、`StartSsoLoginCommand` |
| `Modules.Identity.Contracts\DTOs\` | `SsoConnectionDto`、公開 discovery DTO、MFA continuation DTO。secret は含めない |
| `Modules.Identity.Contracts\Authorization\IdentityPermissions.cs` | SSO 設定、診断、連携管理、policy 管理の permission |
| `Modules.Identity\Domain\` | `SsoConnection`、`ExternalIdentityLink`、`TenantSsoPolicy` |
| `Modules.Identity\Data\Configurations\` | 新 entity の tenant filter、index、長さ制限、関連整合性 |
| `Modules.Identity\Features\v1\Sso\{Feature}\` | `{Feature}CommandHandler.cs`、validator、endpoint を同居 |
| `Modules.Identity\Authentication\Sso\` | OIDC / SAML adapter、bootstrap、scheme registry、transient / replay store |
| `Modules.Identity\Services\` | mapping、eligibility、claims、sign-in completion の共有処理 |
| `Modules.Identity\Localization\` | 新規の SSO エラー・validation keys |
| `Modules.Identity\IdentityModule.cs` | service / options / endpoint / permission 登録。外部 IdP への通信を DbMigrator 起動時に強制しない |
| `src\Host\FSH.Starter.Api\Program.cs` / appsettings | bootstrap の接続、public SSO base URI、feature flag、共有 key ring / store 設定 |
| `src\Host\FSH.Starter.Migrations.PostgreSQL\Identity\` | 新規 migration と Identity snapshot 更新 |
| `clients\dashboard\src\api\sso.ts` / `src\pages\auth\` | HRD、SSO login、callback、MFA continuation |
| `clients\admin\src\api\sso.ts` / `src\pages\` | Phase 1 の operator 管理画面。permission catalog と route gate を同期 |
| `clients\dashboard\src\pages\` | Phase 2 の tenant administrator self-service。dashboard 既存方式の permission hydration / UI gating を使う |
| 各 app の runtime config / routes / auth-context | callback route、共通 login completion、必要な credentialed fetch 設定 |
| `src\Tests\Identity.Tests` / `Integration.Tests` / `Architecture.Tests`、各 app の `tests` | 下記受け入れ試験 |

初期 operator 管理は root の認可された target-tenant 操作として行い、任意の tenant header を持つ一般ユーザーに設定変更を許可しない。secret を read-back できる「接続一覧」は作らない。dashboard は admin の RHF / zod / RouteGuard を持ち込まず、各 app の既存指示に従う。

## 9. 実装フェーズと完了条件

### Phase 0: 設計確定・互換性 PoC

1. 顧客、接続数、Entra の app registration 所有者、認証強度、closed-network IdP、導入形態、失効 SLA を確認する。
2. OIDC / SAML ライブラリのリリース artifact、ライセンス、脆弱性情報、中央 package 依存を確認する。
3. 2 tenant / 2 connection、shared DB / dedicated tenant DB の構成で、callback 前の tenant scope と動的 scheme を PoC する。
4. 同一 site / reverse proxy、必要なら cross-site 配置で、OIDC GET callback と SAML POST callback、cookie / CORS / forwarded headers をブラウザーで試験する。
5. secrets、key ring、原子的 distributed transaction / replay store、cache invalidation の方式を ADR として確定する。
6. 新規 identity mapping と既存 `UserLogins` の比較、本人連携 / 管理者連携、MFA continuation、outbox と DB 更新の整合性を確定する。

完了条件: API の既定認証が Bearer のまま、別 tenant / 別 instance callback の成功と誤 tenant 拒否を示せる。保護された BuildingBlocks に変更が必要なら、変更箇所・理由・影響・代替案を説明し、明示的承認を得るまで該当作業を開始しない。

### Phase 1A: 共通基盤・管理 API

1. SSO feature flag は初期無効、policy は Optional とする。無効時に既存 password / refresh / impersonation が変わらないようにする。
2. entity / EF configuration / migration、Connection の version と unique 制約を追加する。
3. 接続 CRUD、秘密 write-only 更新、設定診断、明示的 user link / unlink、必要な validator / permission / audit を実装する。
4. transaction、handoff、browser binding、return URI allowlist、SSO 専用 rate limit を実装する。
5. eligibility / claims / token finalization の必要な共有処理を抽出し、既存挙動を characterization test で固定する。

完了条件: unauthorized / cross-tenant 設定アクセス、メール一致の無断連携、秘密の read-back、code 二重消費を拒否できる。migration は共有 DB と専用 DB の両方に DbMigrator で適用できる。

### Phase 1B: OIDC の縦断実装

1. scheme / options / metadata を tenant・接続単位で実装する。
2. start → IdP → callback → handoff → 必要な MFA → JWT / refresh / session を接続する。
3. dashboard の HRD / login / callback / MFA と admin の初期管理画面を実装する。
4. Keycloak を用いた自動試験と、Entra OIDC の実接続検証を行う。

完了条件: 正常ログインと refresh に加え、issuer / audience / nonce / state 不一致、別 app / tenant、未連携・停止ユーザー、接続失効、MFA 未完了を拒否できる。IdP token を通常 API の Bearer として受け付けない。

### Phase 1C: SAML と Phase 1 リリース

1. SP metadata、AuthnRequest、ACS、証明書管理、replay validation を追加する。
2. OIDC と同じ mapping / MFA / token finalization / frontend callback を使用する。
3. Entra SAML と Keycloak SAML で実接続検証する。SAML の制約・必要属性・対応 binding を記録する。
4. 運用手順、証明書 rotation、回復手順、通知、別 docs repo の利用ガイド / changelog を更新する。

完了条件: OIDC / SAML の両方で事前連携された顧客ユーザーが SP-Initiated SSO できる。署名・時刻・InResponseTo・replay・XML 攻撃の拒否を自動試験し、Entra の両方式の検証結果を保存する。公開説明に未対応の JIT / SCIM / SLO を含めない。

### Phase 2: 企業運用・ライフサイクル

依存順は接続検証と管理 UX → JIT → SSORequired / 失効 → SCIM、IdP-Initiated / SLO はそれぞれ独立した設計・検証を行う。

- Google Workspace / Okta を両方式で検証し、OneLogin は顧客需要に応じて追加する。
- tenant administrator self-service、設定変更の再認証、検証済み接続の有効化、証明書有効期限通知を実装する。
- opt-in JIT と quota / 同時作成 / conflict 処理を実装する。
- SSORequired、break-glass、接続停止と policy 変更時の refresh / access 失効保証を実装する。
- SCIM 2.0 の Users / Groups、pagination / filtering、PATCH、deactivation を別の詳細計画で定義する。SCIM credential は接続別・tenant scope、hash 保存・rotation・専用 permission / audit とし、一般ユーザー JWT を流用しない。
- SAML IdP-Initiated、必要な OIDC logout / SAML SLO を opt-in で追加する。IdP portal から通常 start route を開く OIDC と、unsolicited SAML response を区別する。

完了条件: tenant 管理者が他 tenant に影響なく設定できる。退職・停止・policy 変更の失効 SLA を実測でき、認証が成功しても SCIM 停止ユーザーはログインできない。

### Phase 3: 顧客要求に応じた拡張

複数有効 IdP、verified-domain discovery、特殊 attribute mapping、AD FS、オンプレミス AD を federation broker / IdP 経由で接続する。LDAP の直接 password 検証、WS-Federation、CAS は実際の契約要件がある場合だけ別 ADR で評価する。

verified-domain discovery を導入するときは tenant catalog 側に最小限の domain → tenant 公開接続参照を持つ Contracts API を設ける。domain 所有確認、共有 domain / 複数 tenant の曖昧性、domain 解放時の失効を設計し、Identity の各専用 DB を横断検索しない。`AppTenantInfo` が BuildingBlocks にあることを理由に無承認でプロパティを追加しない。

## 10. 検証計画・リリースゲート

| レイヤー | 必須試験 |
| --- | --- |
| Unit | mapping の issuer / subject / tenant / connection 境界、メール衝突、MFA、停止ユーザー、最小 role、TTL、config version、return URI、SSORequired / break-glass |
| DB integration | shared DB と dedicated tenant DB、tenant filter / FK / unique 制約、同時 mapping / JIT、migration / seed、設定更新競合 |
| Protocol integration | OIDC の署名・issuer・audience・nonce・state・PKCE・PAR、SAML の署名・audience・Recipient・Destination・InResponseTo・期限・replay・暗号化・XML wrapping |
| Distributed integration | A instance 開始 → B callback → C exchange、二重 callback / exchange、Redis 障害、鍵 rotation、秘密更新・接続停止、全 instance cache invalidation |
| Browser | 別 tenant / app、同時タブ、戻る / 再読込、code 期限切れ、IdP cancellation、MFA、SAML cross-site POST、cookie block、proxy、permissions hydration |
| Regression | password / refresh / role / group / impersonation / locale、root operator / dashboard 境界、SSO feature flag off、SSORequired の全 token 経路 |
| Architecture | request が Contracts、handler / validator / endpoint が runtime、validator の型対応、protocol adapter が Domain に依存逆流しない、Module 境界、対象 assembly の網羅 |
| 運用 | Entra OIDC / SAML の実接続、設定誤り・IdP 障害・証明書失効・ユーザー停止・回復、監査の秘密非露出 |

Playwright の既存 route-mocked テストは UI 挙動に使用するが、IdP リダイレクト・cookie・署名検証の証拠にはしない。別の real-browser / local test IdP の E2E を追加する。CI が外部顧客 IdP の可用性に依存しないよう、自動試験は controlled OIDC / SAML fixture と Keycloak に分け、Entra / Google / Okta は検証環境の接続チェックとして管理する。

Integration.Tests は既存 Testcontainers harness を利用する。Finbuckle context を設定するコードは DbContext / UserManager 呼び出しと同じ async 実行経路で扱う。SSO の新規 Identity entity は既存 `BaseDbContext` 向け isolation test だけでは対象にならないため、IdentityDbContext のモデルと実 query の isolation を明示的に試験する。

実装時の最小検証コマンド例:

```powershell
rtk dotnet test src\Tests\Identity.Tests --filter "FullyQualifiedName~Sso"
rtk dotnet test src\Tests\Architecture.Tests
rtk dotnet test src\Tests\Integration.Tests --filter "FullyQualifiedName~Sso"
rtk dotnet build src\Host\FSH.Starter.Api
rtk dotnet build src\Host\FSH.Starter.DbMigrator
```

selector は実際に追加したテスト名に合わせる。0 件実行を成功と扱わない。frontend は各 app の既存 build / lint / Playwright script を確認して関連 selector を実行し、リリース前に既存 auth regression と必要な広域 suite を実行する。BuildingBlocks を承認の上で変更した場合は保護ルールの full suite 要件にも従う。

## 11. 運用・段階導入・未決事項

### 段階導入

1. additive migration を先に DbMigrator で適用する。
2. feature flag off のアプリを配備し、既存 auth regression を確認する。
3. 検証テナントだけ Optional SSO を有効化する。
4. Entra の両方式と rotation / 回復試験を通過後、顧客へ段階的に公開する。
5. Phase 2 の Required は独立 flag / tenant policy として有効化する。

SSO 無効化を rollback 手段とするが、Required テナントを黙って password login に戻さない。緊急時は承認済み break-glass / operator 手順を使う。mapping / connection を削除する破壊的 rollback は行わない。

監査には tenant、connection、protocol、内部 user ID、操作、結果、理由 code、correlation ID を記録する。client secret、private key、token、assertion、authorization code、RelayState の生値、不要な個人属性は記録しない。SSO 成否率、callback latency、replay 拒否、IdP 通信障害、rotation 期限を観測し、label に user / subject を入れて高 cardinality にしない。

### 実装前に確定する事項

| 決定事項 | 本計画の初期案 / 確定ゲート |
| --- | --- |
| 顧客・認証強度 | 一般企業。MFA とセッション失効 SLA は Phase 0 で決定 |
| IdP app registration | 顧客管理の接続別 client を初期案。SaaS 共有 Entra registration は別評価 |
| SAML ライブラリ | ITfoxtec core を第一候補。PoC / license / advisory 確認で確定 |
| secret / key ring | secret manager 参照優先。配備環境・復元手順を Phase 0 で確定 |
| distributed store | 原子的な一回性を保証する Redis / DB adapter。generic cache だけで代替しない |
| callback URL / 配備 | 固定 HTTPS public base URI と登録済み SPA origin。同一 site 配置を初期推奨 |
| 既存ユーザー連携 | 明示的事前 mapping または再認証済み本人 link。メール自動 link は禁止 |
| IdP MFA の承認 | 初期はローカル TOTP 維持。代替認定は接続別の証拠 mapping を承認後 |
| 即時 JWT 失効 | 既存 JWT TTL と policy / session チェックを比較。保証前に Required / SCIM の SLA を確定 |
| 工数 | Phase 0 完了後に API / UI / protocol / operation の見積もりを作成。PoC 前に納期を断定しない |

本ファイルの作成は計画文書のみであり、SSO の実装、外部 docs repo の更新、接続検証、依存パッケージ採用を完了したことを意味しない。

## 12. 参考資料

以下の公式資料は設計根拠・実装時の再確認先。製品サポート表は実接続試験の結果で決定する。

- [Microsoft: SaaS ISV の SSO 計画](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/plan-sso-integration-isv)
- [Microsoft: シングルサインオンの概要](https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/what-is-single-sign-on)
- [Microsoft: アプリケーションの identity governance 統合](https://learn.microsoft.com/en-us/entra/id-governance/identity-governance-applications-integrate)
- [ASP.NET Core 10: OIDC Web authentication](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0)
- [OpenID Connect Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html)
- [Google: OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect)
- [OASIS: SAML 2.0 specifications](https://docs.oasis-open.org/security/saml/v2.0/)
- [ITfoxtec.Identity.Saml2: 公式 repository / 対応 framework / license](https://github.com/ITfoxtec/ITfoxtec.Identity.Saml2)
- [Sustainsys.Saml2: 公式 repository / 保守 branch](https://github.com/Sustainsys/Saml2)
- [RFC 7643: SCIM Core Schema](https://www.rfc-editor.org/rfc/rfc7643)
- [RFC 7644: SCIM Protocol](https://www.rfc-editor.org/rfc/rfc7644)
