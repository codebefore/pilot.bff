# Pilot BFF

`pilot.bff`, Pilot web arayüzünün browser session ve güvenli API erişim sınırıdır.

## Güncel Durum

İlk servis iskeleti ve kapalı session altyapısı fail-closed olarak hazırdır:

- `.NET 10` minimal API
- `/health` ve `/health/ready`
- `/bff/service-info`
- güvenlik ve cache response header'ları
- daraltılmış Host doğrulaması ve güvenilir proxy listesi olmadan açılamayan forwarded header desteği
- non-root container kullanıcısı
- 256-bit rastgele session id üretimi
- HMAC ile anonimleştirilmiş Redis anahtarları ve Data Protection ile korunan token payload'ı
- Redis'te paylaşılan, PFX sertifikasıyla şifrelenen Data Protection key ring'i
- test projesi
- Dockerfile
- ingress ve NetworkPolicy'si varsayılan kapalı Helm chart
- route-template tabanlı secret-safe request/dependency telemetry

Session store, Identity auth client'ı, browser auth/session endpointleri ve güvenli proxy çekirdeği yerelde hazırdır; `Bff:Session:Enabled` ve `Bff:Proxy:Enabled` varsayılan olarak `false` kalır. Proxy kapalıyken `/bff/v1/*` dahil tanımlanmayan bütün rotalar `404` döner. Browser'a access veya refresh token dönen bir fallback yoktur.

## Yerel Çalıştırma

```bash
dotnet restore Pilot.Bff.sln
dotnet test Pilot.Bff.sln --no-restore
dotnet run --project src/Pilot.Bff.Api/Pilot.Bff.Api.csproj
```

Varsayılan local adres launch profile ile `http://127.0.0.1:5092` olur.

## Endpointler

- `GET /health`
- `GET /health/ready`
- `GET /bff/service-info`
- `GET /bff/antiforgery`
- `POST /bff/auth/otp/request`
- `POST /bff/auth/otp/verify`
- `POST /bff/auth/migration-access/request-code`
- `POST /bff/auth/migration-access/verify-code`
- `GET /bff/session`
- `POST /bff/session/institution`
- `POST /bff/logout`
- `GET|HEAD|POST|PUT|PATCH|DELETE /bff/v1/{service}/{path}` — yalnız proxy özelliği açıkken ve allowlist eşleşirse
- `GET /bff/v1/messaging/hubs/messaging` — yalnız WebSocket upgrade ve allowlist eşleşmesiyle

Session endpointleri feature kapalıyken `503` döner. Unsafe `/bff` istekleri izinli same-origin `Origin`, uygun Fetch Metadata ve `X-CSRF-TOKEN` olmadan çalışmaz. OTP verify cevabı yalnız kullanıcı/kurum session özetini döner; Identity access ve refresh tokenları korumalı Redis kaydından dışarı çıkmaz. Migration approval tokenı da browser'a dönmez: korumalı session kaydında tutulur ve yalnız izinli `*/imports/*` çağrısına server-side eklenir.

## Güvenlik Sınırı

- BFF, web frontend ile aynı origin altında `/bff` path'inden yayınlanacaktır.
- Hedef production adresi `https://app.pilotyanimda.com/bff` olur.
- Access ve refresh token browser'a verilmeyecektir.
- Downstream proxy yalnız açık servis/path/method allowlist'i ve server-side session hazırlandıktan sonra etkinleştirilecektir.
- Domain servislerinin tenant ve permission doğrulamaları korunacaktır.

`X-Forwarded-For` ve `X-Forwarded-Proto` işleme varsayılan olarak kapalıdır. Production'da etkinleştirmeden önce ingress controller'ın gerçek proxy IP'si veya CIDR'ı `Bff__ForwardedHeaders__KnownProxies` ya da `Bff__ForwardedHeaders__KnownNetworks` ile açıkça tanımlanmalıdır. Boş güven listesiyle etkinleştirme uygulamanın başlamasını engeller; tüm ağları güvenilir sayan bir fallback yoktur.

## Session Altyapısını Açma Koşulları

Session modu ancak aşağıdakilerin tümü hazırsa açılır:

- Kalıcı ve yalnız iç ağdan erişilen Redis bağlantısı.
- En az 32 karakterlik ayrı Redis key pepper secret'ı.
- Private key içeren base64 PKCS#12/PFX sertifikası ve parolası.
- Sertifika rotasyonunda eski Redis key-ring kayıtlarını çözmek için geçiş süresince opsiyonel `PreviousCertificateBase64` ve parolası.
- `Redis:Enabled=true` ve `Bff:Session:Enabled=true` ayarları.

Session verisinin Redis TTL'i idle timeout, mutlak session süresi ve Identity refresh-token süresinden en erken olanını aşamaz. İlk rollout kararı 30 dakika idle timeout, 12 saat mutlak süre ve kapalı “beni hatırla” davranışıdır. Redis veya key ring erişilemezse browser token fallback'i yoktur.

Kurum değişimi yeni browser session id üretir ve Redis'teki eski-yeni anahtar geçişi atomik yapılır. OTP doğrulamasından sonra Redis yazımı başarısız olursa yeni Identity refresh tokenı best-effort logout ile geri alınır.

Access token süresi dolmadan önce yenileme, session başına Redis dağıtık kilidi altında yapılır. Kilit sahibi session kaydını tekrar okur; aynı anda gelen istekler dönen yeni kaydı kullanır ve Identity'ye ikinci refresh göndermez. Refresh ve idle touch yalnız anahtar hâlâ varsa atomik güncelleme yapar; logout veya rotation ile silinen session yeniden oluşturulmaz. Logout ve kurum değişimi de aynı session kilidini kullanır. Kilit alınamazsa `503` dönülür ve browser token fallback'i oluşmaz.

Bu akışların unit/race testleri ile gerçek Redis üzerinde çoklu replica key-ring/session round-trip, dağıtık lock, atomik rotation, Redis kesintisinde fail-closed davranış ve PFX rollover testi hazırdır. Özellik yine varsayılan kapalıdır.

Yerel container image smoke testinde Helm ile aynı production Host ayarı kullanılarak liveness, readiness ve service-info doğrulandı. Image runtime'da UID/GID `1654` ile non-root çalışır; session ve proxy varsayılan kapalı kalır, güvenilmeyen Host reddedilir.

Gerçek Redis testini çalıştırmak için yalnız yerel/test Redis bağlantısı verilir:

```bash
BFF_TEST_REDIS_CONNECTION='127.0.0.1:6379,abortConnect=false' \
  dotnet test Pilot.Bff.sln --filter FullyQualifiedName~RedisSessionIntegrationTests
```

Kesinti testi ayrıca `BFF_TEST_REDIS_OUTAGE_CONNECTION` ile, bağlantı noktası kontrollü olarak erişilemezken çalıştırılır. Bu değişkenler yoksa normal unit suite harici bağımlılık kullanmaz.

Gerçek Gateway zincir testi, build edilmiş `Pilot.Gateway.Api.dll` yolunu alır; test yerel bir downstream sunucusu açar, Gateway process'ini geçici olarak başlatır ve BFF → Gateway → servis dönüşümünü doğrular:

```bash
BFF_TEST_GATEWAY_DLL='../pilot.gateway/src/Pilot.Gateway.Api/bin/Release/net10.0/Pilot.Gateway.Api.dll' \
  dotnet test Pilot.Bff.sln --filter FullyQualifiedName~RealGatewayIntegrationTests
```

Bu test `/bff/v1/candidates/...` isteğinin gerçek Gateway tarafından `/api/candidates/...` yoluna dönüştürüldüğünü ve yalnız server-side bearer ile aktif kurum başlığının downstream'e ulaştığını kontrol eder. Değişken verilmezse normal suite harici process başlatmaz.

Proxy hedefi yalnız `Bff:Proxy:GatewayBaseUrl` ile verilen sabit HTTP(S) origin'idir. İstemci hedef host/scheme/port seçemez. Servis, path ve method allowlist'i `internal`, health, metrics, Swagger/OpenAPI ve yönetim rotalarını reddeder. Browser'dan gelen `Authorization`, `Cookie`, `X-Institution-Id`, `Host`, `X-Forwarded-*` ve iç servis başlıkları upstream'e taşınmaz; Authorization ve aktif kurum yalnız server-side session'dan üretilir. Redirect/cookie takibi kapalıdır. Body limiti antiforgery/body okumasından önce uygulanır.

Varsayılan request body sınırı 10 MiB'dir. Yalnız aktif Document multipart sözleşmelerinde, doğru method/path ve geçerli kaynak GUID'leri eşleştiğinde standart 10 MiB dosyalar için 11 MiB request, 25 MiB eğitmen atama evrakı için 26 MiB request kabul edilir. JSON, bilinmeyen rota, yanlış method ve migration upload'ları bu istisnayı alamaz. Eski 110 MiB Wenntec dump endpoint'i Finance servisinde kaldırıldığı için BFF'de yeniden açılmaz.

Trafik koruması, doğrulanmış user + tenant + güvenilir `RemoteIpAddress` birleşimine göre read ve mutation isteklerini ayrı sınırlar. Session/Redis açıkken rate penceresi ve concurrency lease'leri Redis Lua ile tüm BFF replica'ları arasında global uygulanır; Redis server time kullanılır ve düşen isteklerin lease'i süre sonunda kendini temizler. Varsayılan değerler read için 120 istek/dakika ve 50 eşzamanlı istek; mutation için 30 istek/dakika ve 10 eşzamanlı istektir. Limit aşımı kuyruk oluşturmadan `429` döner. Session kapalı test modunda yalnız process-local uygulama kullanılır.

Helm chart ayrıca varsayılan kapalı ingress-nginx IP koruması sunar. `ingress.rateLimit.enabled=true` olduğunda `limit-rps`, burst multiplier ve connection annotation'ları üretilir; ingress body-size 26m olur. ingress-nginx limitleri controller replica başına ek edge savunmasıdır; uygulama kotasının yerini almaz.

Proxy, upstream response header'ını en fazla `Bff:Proxy:TimeoutSeconds` içinde bekler; bağlantı timeout'u `504`, bağlantı hatası `502` olur. Header geldikten sonraki dosya/stream kopyası ayrı `ResponseStreamingTimeoutSeconds` ile sınırlıdır (varsayılan 300 saniye). `Range`, `Accept-Ranges` ve `Content-Range` korunur; response gövdesi buffer'lanmadan aktarılır.

Structured request logları yalnız endpoint route template'ini kullanır; ham path, query, header, cookie veya body kaydetmez. Her cevap `X-Pilot-Trace-Id` taşır ve request/dependency duration metrikleri üretir. Helm NetworkPolicy açıldığında ingress-nginx girişini; aynı namespace uygulama servisleri, tanımlı Redis CIDR'ları ve DNS çıkışını allowlist eder.

Mimari ve rollout planı `pilot.context/03-uygulama/bff-ve-ai-guvenlik-plani.md` belgesindedir.

## Image Yayını

`.github/workflows/publish-image.yml` yalnız `main` branch'inde manuel olarak
çalıştırılabilir. Workflow önce gerçek Redis entegrasyon testleri, format/build
kontrolü ve Helm doğrulamasını tamamlar; ardından yalnız
`ghcr.io/codebefore/pilot-bff:sha-<40-karakter-commit>` immutable image'ını
yayınlar. Herhangi bir cluster deploy'u, GitOps değişikliği, SSH veya production
sync adımı içermez.
