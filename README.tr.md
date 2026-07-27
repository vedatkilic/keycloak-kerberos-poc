[English](README.md) · **Türkçe**

# Keycloak Kerberos SSO — PoC (macOS / Linux / Windows)

Uçtan uca **parolasız (Kerberos SSO) Keycloak entegrasyonunu** gösteren bir demo. İstemci kodu
(ConsoleClient, WpfClient) ve akışın tamamı **macOS, Linux ve Windows'ta** aynı şekilde çalışır —
zaten Windows asıl production hedefidir. Başlıktaki `(macOS / Linux / Windows)` notu yalnızca Docker
demo harness'ının üçünde de denendiğini belirtir; burada macOS/Linux'a özgü hiçbir şey yoktur.

Demo, Windows domain ortamı olmadan da çalıştırılabilir: Windows'ta "kullanıcının sabah domain'e login
olması" ne ise, burada `kinit` odur; akışın geri kalanı production ile aynı protokolü kullanır. Gerçek,
domain'e katılı bir Windows PC'de `kinit`'i tamamen atlarsınız — bkz.
[Domain'e katılı bir Windows PC'den test](#domaine-katılı-bir-windows-pcden-test-zaten-login-olmuş-kullanıcı).

    Mac (istemci: kinit + .NET console / Chrome)
        |-- :88  Kerberos ------> KDC (Docker, MIT Kerberos)
        |-- :8080 HTTP ---------> Keycloak (Docker, keytab ile SPNEGO)
        |-- :5080 HTTP ---------> BackendApi (dotnet, JWT bearer)

Aynı backend'e karşı iki parolasız yöntem gösterilir:
1. **Kerberos SSO** — *kullanıcı* doğrulanır (bir masaüstü uygulaması, public client + PKCE).
2. **JWT Bearer grant (RFC 7523)** — güvenilen bir *backend servisi* kendi JWT assertion'ını
   imzalayarak kendini doğrular (bkz. [Yöntem 2](#yöntem-2--jwt-bearer-grant-rfc-7523)).

## Gereksinimler
- Docker Desktop
- .NET 9 SDK
- macOS'ta Kerberos araçları hazır gelir (kinit/klist); Linux'ta `krb5-user` kurun.
- Keycloak 26.7 (docker-compose.yml'de sabitli). JWT Bearer grant, 26.5+ ve
  `jwt-authorization-grant` feature'ını gerektirir; compose komutunda zaten açık.

## Kurulum (bir kez)

    docker compose up -d --build          # KDC + Keycloak
    ./keycloak/setup-realm.sh             # realm, Kerberos federation, boa-desktop + JWT Bearer (wcf-issuer, boa-wcf)
    ./client-mac/setup-mac.sh             # /etc/hosts + krb5 config + Chrome izni

## Demo akışı

    export KRB5_CONFIG=$PWD/client-mac/krb5-client.conf

    # 1) Henüz "login" olmadık -> akış login formuna düşer (fallback kanıtı)
    kdestroy 2>/dev/null
    cd src/ConsoleClient && dotnet run     # 200 döner, formun açıklamasını yazar

    # 2) "Windows'a login olduk"
    kinit vedat                            # parola: test123
    klist                                  # TGT görünmeli

    # 3) Backend'i ayrı bir terminalde başlat
    (cd src/BackendApi && dotnet run)

    # 4) Parolasız token + yetkili API çağrısı
    cd src/ConsoleClient && dotnet run

Beklenen çıktı: bir code alınır (parola sorulmadan), access/refresh token'lar yazılır ve
`/api/hello` kullanıcı adıyla cevap verir.

Chrome ile aynı SSO: `http://keycloak.bank.local:8080/realms/bank/account` adresine gidin —
parolasız girebilmelisiniz (setup-mac.sh Chrome iznini verdi; Chrome'u yeniden başlatın).

## Yöntem 2 — JWT Bearer grant (RFC 7523)

Farklı bir aktör, yine parolasız: güvenilen bir **backend servisi** (production'da bir WCF
servisi) *kendini* doğrular. Kendi JWT "assertion"ını bir private key ile imzalar ve Keycloak'ın
token endpoint'inde gerçek bir access token ile takas eder — kullanıcı yok, tarayıcı yok,
Kerberos yok:

    assertion (backend imzalar) --> Keycloak /token (grant_type=jwt-bearer) --> access_token

Keycloak imzayı `wcf-issuer` identity provider'ına karşı doğrular ve iddia edilen kullanıcıyı
bir federated-identity link üzerinden çözer. `setup-realm.sh` demo public key'i
(`pki/wcf-issuer.pub`) zaten kaydetti, confidential `boa-wcf` client'ını oluşturdu ve `wcf-user`
kullanıcısını harici subject `wcf-user-001`'e linkledi. (Kerberos demosunun `vedat`'ından ayrı
bir kullanıcı; böylece iki akış tek bir kullanıcı adı üzerinde çakışmaz.)

    # Backend çalışıyor (verilen token'ı doğrular)
    (cd src/BackendApi && dotnet run)

    # "WCF backend": bir assertion imzalar, token alır, API'yi çağırır
    cd src/JwtBearerClient && dotnet run

Beklenen çıktı: assertion imzalanır, bir Keycloak access token geri gelir (`aud=boa-api`) ve
`/api/hello` `wcf-user` ile cevap verir. Bu akış da `keycloak.bank.local` kullanır, bu yüzden
`setup-mac.sh`'in eklediği `/etc/hosts` kaydı gerekir (verilen token'ın issuer'ı, backend'in
yapılandırılmış authority'siyle eşleşmeli). Başka yere yönlendirmek için `KC_BASE`/`API_BASE`
değişkenlerini ezin.

> `pki/` içindeki imzalama anahtarı yalnızca demo içindir. Production'da private key WCF servisinde
> (veya bir HSM'de) kalır, public key bir JWKS URL üzerinden yayınlanır ve anahtar rotasyona tabidir.

## Sorun giderme (hızlı)
| Belirti | Kontrol |
|---|---|
| Konsolda 200 / login formu | `klist` boş mu? `kinit vedat` çalıştırıldı mı? `KRB5_CONFIG` export edildi mi? |
| `Server not found in Kerberos database` | /etc/hosts kaydı ve IP değil `keycloak.bank.local` üzerinden erişim |
| Keycloak log: keytab hatası | `docker compose down -v && up --build` (keytab volume'unu tazeler) |
| Keycloak log: `Cannot locate KDC` | Keycloak'ın Java'sı `KRB5_CONFIG`'i değil `/etc/krb5.conf`'u okur (docker-compose.yml'de mount) |
| Kerberos: `302 ama code yok` (`execution=VERIFY_PROFILE`) | Import edilen kullanıcının profili yok; `setup-realm.sh` VERIFY_PROFILE required action'ını kapatır — yeniden çalıştırın |
| .NET Negotiate üretmiyor | GSS trace'i alın: `KRB5_TRACE=/dev/stdout dotnet run` |
| Chrome formu gösteriyor | `defaults read com.google.Chrome AuthServerAllowlist` + Chrome'u yeniden başlat |
| JWT Bearer: `invalid_grant` / `Invalid signature` | `setup-realm.sh` çalıştı mı? `pki/wcf-issuer.pub`, assertion'ı imzalayan anahtarla eşleşiyor mu? |
| JWT Bearer: `invalid_grant` (kullanıcı) | İddia edilen `sub`, `wcf-issuer`'a linkli olmalı (setup-realm `wcf-user` ↔ `wcf-user-001` linkler) |

## Production'a eşleme
| PoC (bu repo) | Production (banka) |
|---|---|
| MIT KDC container | Active Directory (KDC) |
| `kadmin addprinc + ktadd` | `New-ADUser` + `setspn` + `ktpass` (AES256) |
| Bağımsız Kerberos federation | LDAP federation + Kerberos entegrasyonu |
| `kinit vedat` | Windows domain login (otomatik) |
| `defaults write ... AuthServerAllowlist` | GPO: AuthServerAllowlist + DisableAuthNegotiateCnameLookup |
| ConsoleClient (headless) | Aynı kod + `src/WpfClient.Sample` (görünmez WebView2 katmanı) |
| `JwtBearerClient` + `pki/wcf-issuer.key` (commitli) | WCF servisi; private key servis/HSM'de, JWKS ile yayınlı, rotasyonlu |
| Statik public key'li `wcf-issuer` IdP | `useJwksUrl=true` + issuer'ın JWKS URL'i ile `wcf-issuer` IdP |
| http / dev mode | https, gerçek sertifika, prod mode |

`src/WpfClient.Sample` yalnızca Windows'ta derlenir; production istemcisinin (katmanlı, penceresiz
token alımı) referans kodudur. Mac demosu için gerekli değildir.

> Bu repo bir proof of concept'tir; parolalar ve `RequireHttpsMetadata=false` gibi ayarlar sadece
> demo içindir ve production'da kullanılmaz.

---

# Mimari, bileşenler ve production'a taşıma

> Bu bölüm ekibe/müşteriye yönelik açıklamadır: hangi uygulama neyi temsil ediyor, hangi
> kütüphaneler kullanıldı, PoC'de nasıl çalıştı, Windows'ta müşteri makinesinde nasıl çalışacak
> ve production'da nelerin değişmesi gerekiyor.

## 1. Bileşenler — hangi uygulama neyi temsil ediyor

| Bileşen (repo) | Neyi temsil ediyor | PoC'deki rolü | Production karşılığı |
|---|---|---|---|
| **KDC** (`kdc/`, Docker, MIT Kerberos) | Kimlik doğrulayan bilet dağıtıcı (KDC) | `kinit` ile TGT üretir, keytab basar | **Active Directory** (kurumun Domain Controller'ı — KDC de odur) |
| **Keycloak** (`keycloak/`, Docker 26.7) | Kurumsal IdP / OIDC token sunucusu | SPNEGO'yu keytab ile doğrular, `authorization_code`→token, `jwt-bearer`→token verir | Kurumsal Keycloak cluster'ı (AD'ye LDAP + Kerberos federation ile bağlı) |
| **ConsoleClient** (`src/ConsoleClient/`) | Masaüstü uygulamanın **çekirdek token alma mantığı** (headless) | Tarayıcısız Negotiate ile `authorization_code` + PKCE akışını sürer | Aynı mantık, WPF/WinForms uygulamasının içinde çalışır |
| **WpfClient.Sample** (`src/WpfClient.Sample/`) | Gerçek **Windows masaüstü istemcisi** (referans kod) | Sadece Windows'ta derlenir; katmanlı/görünmez token alımı | Müşteriye dağıtılan asıl uygulama |
| **JwtBearerClient** (`src/JwtBearerClient/`) | Güvenilen **WCF backend servisi** | Kendi private key'iyle assertion imzalar, `jwt-bearer` ile token alır | Gerçek WCF servisi (private key servis/HSM içinde) |
| **BackendApi** (`src/BackendApi/`) | Korunan **kaynak API** (Resource Server) | Bearer JWT imzasını JWKS ile lokal doğrular; Kerberos'tan haberi yok | Kurumun mikroservis/API'leri (aynı JWT doğrulama) |
| **PKI** (`pki/`) | WCF issuer'ın imzalama anahtar çifti | Demo public/private key (repo'ya commitli) | Private key WCF/HSM'de, public key JWKS URL'de, rotasyonlu |

Önemli tasarım noktası: **BackendApi'de tek satır Kerberos kodu yok.** Backend sadece "geçerli
imzalı bir JWT geldi mi?" diye bakar. Kullanıcının nasıl doğrulandığı (Kerberos SSO mu, WCF
assertion'ı mı) tamamen Keycloak'ın işidir. Bu, iki farklı parolasız yöntemin aynı API'yi hiç
değiştirmeden kullanabilmesini sağlar.

## 2. Kullanılan kütüphaneler ve neden

| Katman | Kütüphane / teknoloji | Ne için |
|---|---|---|
| Masaüstü token alımı (headless) | .NET `HttpClient` + `HttpClientHandler` + `CredentialCache` | SPNEGO/Negotiate handshake'i işletim sisteminin Kerberos yığını üzerinden otomatik yapmak |
| PKCE | `System.Security.Cryptography` (SHA256, `RandomNumberGenerator`) | `code_verifier`/`code_challenge` üretmek — public client için zorunlu |
| WPF referans istemci | `IdentityModel.OidcClient` (6.x) | OIDC `authorization_code`+PKCE akışını, silent refresh ve 401→retry'ı hazır vermek |
| WPF gömülü tarayıcı | `Microsoft.Web.WebView2` | Görünmez WebView2; SSO cookie'sini taşımak, gerekince kullanıcıya form göstermek |
| WCF assertion | `System.Security.Cryptography.RSA` (RS256, PKCS#1) | RFC 7523 JWT assertion'ını imzalamak |
| BackendApi | `Microsoft.AspNetCore.Authentication.JwtBearer` | Token imzasını IdP'nin JWKS'i ile lokal doğrulamak (`Authority` + `ValidAudience`) |
| IdP | Keycloak 26.7 + `jwt-authorization-grant` feature | SPNEGO federation + RFC 7523 JWT Bearer grant |
| KDC | MIT Kerberos (Docker) | AD'nin yokluğunda TGT/keytab üretmek |

## 3. `authorization_code` + PKCE + redirect akışı nasıl yönetiliyor

Kullanıcı akışı (Kerberos SSO) tam olarak standart OIDC Authorization Code + PKCE'dir; tek fark
parola sorulmamasıdır. Adımlar (`src/ConsoleClient/Program.cs`):

1. **PKCE üret:** rastgele `code_verifier`, ondan SHA256 ile `code_challenge` (`S256`).
2. **`/auth` çağrısı:** `client_id=boa-desktop&response_type=code&redirect_uri=...&code_challenge=...`.
   `HttpClientHandler.Credentials`'a `Negotiate` şeması pinlenir (`CredentialCache`), böylece
   Keycloak `401 WWW-Authenticate: Negotiate` dönünce .NET **otomatik** olarak OS'un aktif Kerberos
   biletiyle bir SPNEGO token'ı üretip `Authorization: Negotiate <token>` header'ıyla yeniden ister.
   NTLM/Basic fallback'i kapatmak için şema özellikle `Negotiate`'e sabitlenir.
3. **302 redirect:** Keycloak kullanıcıyı doğrular ve `redirect_uri`'ye `?code=...` ile yönlendirir.
   `AllowAutoRedirect = false` olduğu için istemci redirect'i takip etmez; `Location` header'ındaki
   `code`'u query'den okur. **Redirect adresine gerçek bir istek asla gitmez** — loopback URL
   (`http://127.0.0.1:.../cb`) sadece kodun taşındığı bir işaret noktasıdır. (WPF tarafında
   `LayeredBrowser` bunu `NavigationStarting`'de `e.Cancel = true` ile yapar; hiç HTTP isteği atmadan
   URL'den kodu çeker.)
4. **`/token` çağrısı:** `grant_type=authorization_code` + `code` + `code_verifier`. Keycloak
   verifier'ı challenge ile eşleştirir (PKCE doğrulaması) ve `access_token` + `refresh_token` döner.
5. **API çağrısı:** access token `Authorization: Bearer ...` ile BackendApi'ye gönderilir.

Neden loopback redirect + PKCE? `boa-desktop` **public client**'tır (client secret'ı yok — masaüstü
uygulamasında secret güvenle saklanamaz). PKCE, araya giren birinin çalınan `code`'u kullanmasını
engeller; loopback (`127.0.0.1`) OAuth için native uygulama önerilen yöntemdir (RFC 8252).

## 4. Windows makinesinde nasıl çalışır / müşteri nasıl çalıştırır

macOS PoC'de `kinit vedat` yaptığımız an, **Windows'ta kullanıcının sabah domain'e login olduğu
an**'a karşılık gelir. Domain'e katılı bir Windows makinesinde kullanıcı zaten bir TGT sahibidir;
ekstra hiçbir şey yapmaz. Kod tarafında da değişiklik yoktur — aynı `HttpClient` + `Negotiate`
mantığı Windows'ta OS'un LSA/Kerberos yığınını kullanır.

**Önkoşullar (müşteri makinesi):**
- Makine kurumsal **AD domain'ine katılı** ve kullanıcı domain hesabıyla login olmuş olmalı.
- **.NET 9 Desktop Runtime** kurulu olmalı (WPF istemcisi için). ConsoleClient için .NET 9 Runtime yeterli.
- Keycloak host adı (`keycloak.bank.local` yerine gerçek FQDN) **Negotiate allowlist**'inde olmalı.
  Bu, kurumda tek tek makinelerde `defaults write` değil, **GPO** ile dağıtılır:
  - `AuthServerAllowlist` = IdP FQDN'i
  - `DisableAuthNegotiateCnameLookup` = önerilir (CNAME arkasında SPN sorunlarını önler)
  - Chrome/Edge için aynı politika (`AuthServerAllowlist`), WebView2 için uygulama kendi
    `--auth-server-allowlist` argümanını verir (`LayeredBrowser.cs`), GPO gerekmez.
- Keycloak'ta HTTP/FQDN için bir **SPN + keytab** tanımlı olmalı (production'da `ktpass` ile AES256).

**Çalıştırma (production-şeklindeki WPF istemcisi):**
```
# Domain'e login olmuş kullanıcı; uygulama açılır, arka planda görünmez şekilde token alır.
WpfClient.Sample.exe
```
`KeycloakAuthService.SignInAndCreateApiClientAsync()` çağrıldığında:
1. `LayeredBrowser` WebView2'yi **ekran dışında** (görünmez) açar ve `/auth`'a gider.
2. Windows'un mevcut Kerberos biletiyle SPNEGO otomatik tamamlanır → Keycloak redirect'e döner.
3. `NavigationStarting` redirect URL'ini yakalar, isteği iptal eder, kodu okur → pencere hiç
   görünmeden kapanır. Kullanıcı **hiçbir şey görmez** (parolasız, penceresiz).
4. Sadece etkileşim gerekiyorsa (parola süresi dolmuş, OTP kaydı vb.) `NavigationCompleted`
   tetiklenir ve pencere **o an** ekranın ortasına getirilip gösterilir — katmanlı yaklaşım.

**Çalıştırma (headless doğrulama, ConsoleClient ile):** WPF'e gerek kalmadan aynı token mantığını
Windows'ta test etmek için:
```
set KC_BASE=https://keycloak.bank.local/realms/bank/protocol/openid-connect
set API_BASE=https://boa-api.bank.local
dotnet run --project src\ConsoleClient
```
Domain kullanıcısı olarak çalıştırıldığında parola sorulmadan token gelir.

### Domain'e katılı bir Windows PC'den test (zaten login olmuş kullanıcı)

"Windows'a domain hesabımla zaten login oldum — sadece çalıştırıp parolasız token alabilir miyim?"
Cevap **hangi Keycloak'a bağlandığına** göre değişir, çünkü Windows login'iniz size **yalnızca kendi
AD domain'iniz** için bir Kerberos TGT verir.

**Oturumunuzda zaten ne var.** Normal bir domain login'inden sonra Windows, LSA içinde bir TGT tutar.
Kontrol edin — `klist` Windows'ta yerleşik gelir (`kinit` yok, `KRB5_CONFIG` yok; bunlar sadece
Mac/Linux içindir — Windows'ta .NET ve tarayıcılar bu biletleri SSPI/LSA üzerinden otomatik kullanır):
```
whoami /upn          :: domain kimliğiniz, ör. vedat@CORP.EXAMPLE.COM
klist                :: önbellekteki biletler: bir krbtgt/CORP.EXAMPLE.COM kaydı görmelisiniz
```

**A) *Sizin* AD'nize federe edilmiş bir Keycloak'a karşı — gerçek, parolasız test.** Mevcut login'inizin
gerçekten sıfır-istem SSO ürettiği tek kurulum budur. Sunucu tarafında şunları gerektirir: Keycloak'ın
AD'nize LDAP + Kerberos federation ile bağlı olması (`serverPrincipal`/keytab'ı *sizin* realm'iniz için,
ör. `HTTP/keycloak.corp.example.com@CORP.EXAMPLE.COM`), Keycloak host'u için AD'de kayıtlı bir SPN ve
Keycloak FQDN'inin makinenizin tarayıcı Negotiate allowlist'inde olması. Sonra doğrulayın (parola
beklenmiyor):
```
:: 1) Keycloak'ın SPN'i için servis bileti alabiliyor musunuz? (SPN var ve erişilebilir mi kanıtlar)
klist get HTTP/keycloak.corp.example.com

:: 2) Tarayıcı testi — Edge/Chrome, account console'u login formu OLMADAN açar
start https://keycloak.corp.example.com/realms/bank/account

:: 3) Masaüstü istemcisinin çekirdek mantığı, headless:
set KC_BASE=https://keycloak.corp.example.com/realms/bank/protocol/openid-connect
set API_BASE=https://boa-api.corp.example.com
dotnet run --project src\ConsoleClient

:: 4) Çalıştıktan sonra klist artık HTTP/keycloak.corp.example.com için bir bilet listelemeli
klist
```
1. adım başarısızsa (`no such SPN`, yanlış encryption type) sunucu SPN/keytab'ı kurulmamıştır — bu bir
sunucu işidir, istemci değil. Tarayıcı login formu gösteriyorsa FQDN allowlist'te değildir. Test için
GPO'suz, makine-başı hızlı tarayıcı allowlist'i (sonra tarayıcıyı yeniden başlatın):
```
reg add "HKCU\Software\Policies\Google\Chrome"    /v AuthServerAllowlist /t REG_SZ /d "keycloak.corp.example.com" /f
reg add "HKCU\Software\Policies\Microsoft\Edge"   /v AuthServerAllowlist /t REG_SZ /d "keycloak.corp.example.com" /f
```

**B) Bu repodaki Docker PoC Keycloak'ına karşı (`BANK.LOCAL` / MIT KDC).** Burada Windows domain
login'iniz **yardımcı olmaz**: PoC'nin KDC'si `BANK.LOCAL`, AD'nizin hiç tanımadığı ayrı bir realm'dir,
bu yüzden TGT'niz `HTTP/keycloak.bank.local@BANK.LOCAL` için servis bileti alamaz. Akışı çalıştırmak
için `BANK.LOCAL`'a el ile kimlik doğrulamanız gerekir — Mac demosunun yaptığının aynısı, sadece
Windows'ta:
- **MIT Kerberos for Windows** kurun (MIT realm için `kinit` sağlar).
- `C:\Windows\System32\drivers\etc\hosts` dosyasına `127.0.0.1 keycloak.bank.local kdc.bank.local` ekleyin.
- `BANK.LOCAL` biletini el ile alın, sonra istemciyi çalıştırın:
  ```
  set KRB5_CONFIG=%CD%\client-mac\krb5-client.conf
  kinit vedat        :: parola: test123 — bu, "zaten login olmuş" varsayımının YERİNE geçer
  dotnet run --project src\ConsoleClient
  ```
Bu, *istemci kodunu* çalıştırmak için uygundur, ama gerçek domain login'inizi **kullanmaz** — Mac'teki
gibi el ile bir `kinit`'tir. Gerçek "Windows'a login oldum ve kendiliğinden çalıştı" testi için (A)
kurulumu gerekir.

**Tek cümleyle:** Windows-login'inizden parolasız SSO, yalnızca bağlandığınız Keycloak sizin AD
domain'inize güveniyorsa çalışır. Docker PoC `BANK.LOCAL`'a güvenir, sizin kurum domain'inize değil;
bu yüzden ona karşı her istemci (Mac ya da Windows) el ile `kinit` yapmak zorundadır.

> Not: `src/WpfClient.Sample` yalnızca Windows'ta (`net9.0-windows`, `UseWPF`) derlenir. Mac
> demosunda derlenmesine gerek yoktur; production istemcisinin referans kodudur.

## 5. WCF backend bilgisi nasıl alınıp işleniyor (RFC 7523 JWT Bearer)

İkinci parolasız yöntem **kullanıcı değil, güvenilen bir servisin** kendini doğrulamasıdır. Burada
Kerberos yoktur; WCF servisi kendi kimliğiyle konuşur (`src/JwtBearerClient/Program.cs`):

1. **Private key yüklenir** (PoC'de `pki/wcf-issuer.key`; production'da servis/HSM içinde).
2. **Assertion JWT üretilir ve RS256 ile imzalanır:** `iss` (issuer kimliği), `sub` (hangi
   kullanıcı/servis adına — dış kullanıcı id'si), `aud` (realm issuer URL'i), kısa `exp` (≤300s),
   tek kullanımlık `jti`.
3. **`/token` çağrısı:** `grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer` + `assertion` +
   `client_id=boa-wcf` + `client_secret`.
4. **Keycloak doğrular:** imzayı `wcf-issuer` identity provider'ında kayıtlı public key ile kontrol
   eder, `sub`'ı **federated-identity link** üzerinden gerçek Keycloak kullanıcısına (`wcf-user`)
   çözer ve gerçek bir `access_token` (`aud=boa-api`) döner.
5. Bu token da BackendApi'ye Bearer olarak gider — API için iki akış ayırt edilemez.

Yani "Windows/WCF tarafındaki bilgi" iki farklı şekilde taşınıyor:
- **Kullanıcı SSO'sunda:** bilgi = kullanıcının Kerberos **TGT/service ticket**'ı, SPNEGO header'ıyla
  taşınır; Keycloak keytab ile çözer.
- **WCF servisinde:** bilgi = servisin **imzaladığı JWT assertion**'ı; Keycloak public key ile
  doğrular. Servis, adına konuştuğu kullanıcıyı assertion'ın `sub`'ında bildirir.

## 6. Production'da PoC'den farklı ne bekleniyor

Kısa cevap: **akış ve kod aynı, altyapı ve güvenlik ayarları gerçekleşir.** (Detaylı eşleme için
yukarıdaki "Production'a eşleme" tablosu.) Başlıca farklar:

- **KDC → Active Directory:** `kinit`/`kadmin` yerine domain login otomatik; SPN/keytab `setspn` +
  `ktpass` (AES256) ile üretilir. Keycloak, AD'ye LDAP + Kerberos federation ile bağlanır.
- **HTTP → HTTPS:** PoC `http` ve `RequireHttpsMetadata=false` kullanır. Production'da gerçek
  sertifika, `https`, Keycloak `start` (prod mode) — `start-dev` değil.
- **Allowlist dağıtımı:** tek tek `defaults write` değil, kurum genelinde **GPO**.
- **WCF anahtarı:** commitli demo key yerine private key **WCF/HSM içinde**; public key **JWKS URL**
  üzerinden yayınlanır ve `wcf-issuer` IdP'sinde `useJwksUrl=true` ile okunur; anahtar **rotasyona**
  tabi. (PoC'de statik public key ve `useJwksUrl=false`.)
- **Assertion tekrar kullanımı:** production'da `jti` + kısa `exp` ile replay koruması zaten açık;
  ayrıca `AssertionReuseAllowed=false` korunmalı.
- **Profil/required actions:** PoC, Kerberos'tan import edilen kullanıcıda profil formu olmadığı için
  `VERIFY_PROFILE`'ı kapatır. Production'da kullanıcı profilleri LDAP'tan geldiğinden bu adım gerekmez.
- **Kimlik doğrulama akışı:** SPNEGO execution PoC'de `ALTERNATIVE`'e çekilir ki bilet yoksa login
  formuna düşebilsin (fallback). Production'da da fallback istenir; bu ayar korunur.

Özetle production, PoC'deki **iki akışın da aynı kodla** çalışmasını bekler; değişen tek şey demo
kısayollarının (parola, http, commitli key, elle allowlist) gerçek kurumsal karşılıklarıyla (domain,
https, HSM/JWKS, GPO) değiştirilmesidir.
