# Captcha solving service

> ⚠️ **Юридическое предупреждение:** Этот модуль предназначен исключительно для использования на сайтах, которыми вы владеете, или для которых у вас есть явное письменное разрешение владельца. Использование данного кода для обхода CAPTCHA на чужих ресурсах запрещено и может быть незаконным.

Модуль `Core.Services.Captcha` инкапсулирует работу с внешними API распознавания CAPTCHA. Он предоставляет единый интерфейс, который может использоваться в бекенд- и фронтенд-сценариях с соблюдением ограничений по скорости и безопасности.

## Интерфейс

```csharp
var logger = /* ILogger */;
var captchaService = CaptchaService.CreateFromEnvironment(logger);

var token = await captchaService.SolveCaptchaAsync(
    cancellationToken,
    new CaptchaRequest {
        Type = CaptchaType.RecaptchaV2,
        SiteUrl = "https://example.com",
        SiteKey = "site-key"
    });
```

Метод `SolveCaptchaAsync` автоматически применяет:

- ограничение параллелизма и rate limit;
- таймауты и повторные попытки (экспоненциальный backoff);
- логирование событий без утечки секретов.

По умолчанию модуль работает в режиме `mock` и не делает сетевых запросов. Для включения реальных провайдеров необходимо настроить переменные окружения.

## Переменные окружения

| Переменная | Назначение | Значение по умолчанию |
| --- | --- | --- |
| `CAPTCHA_PROVIDER` | Провайдер (`mock`, `2captcha`, `anticaptcha`) | `mock` |
| `CAPTCHA_API_KEY` | API-ключ провайдера | — |
| `CAPTCHA_PROVIDER_URL` | Пользовательский базовый URL API | `https://api.2captcha.com/` |
| `CAPTCHA_MAX_PARALLEL_TASKS` | Максимум параллельных задач | `2` |
| `CAPTCHA_REQUESTS_PER_WINDOW` / `CAPTCHA_REQUESTS_PER_MINUTE` | Лимит запросов за окно | `10` |
| `CAPTCHA_RATE_LIMIT_WINDOW_SECONDS` | Размер окна в секундах | `60` |
| `CAPTCHA_REQUEST_TIMEOUT_SECONDS` | Таймаут HTTP-запросов | `120` |
| `CAPTCHA_POLLING_INTERVAL_SECONDS` | Интервал опроса результата | `5` |
| `CAPTCHA_MAX_RETRY_ATTEMPTS` | Кол-во попыток решения | `3` |
| `CAPTCHA_INITIAL_RETRY_DELAY_SECONDS` | Начальная задержка перед ретраем | `2` |
| `CAPTCHA_RETRY_BACKOFF` | Множитель экспоненциального backoff | `2.0` |
| `CAPTCHA_MOCK_RESPONSE` | Ответ mock-провайдера | `mock-solution` |
| `ALLOW_CAPTCHA_IN_PROD` | Разрешить выполнение в production | `false` |

> 🔐 Для прод-окружений необходимо явно выставить `ALLOW_CAPTCHA_IN_PROD=true` и документировать письменное разрешение владельца ресурса.

## Провайдеры

### Mock

Используется по умолчанию. Возвращает значение `CAPTCHA_MOCK_RESPONSE`, не выполняет сетевых запросов и подходит для тестов и локальной разработки.

### 2Captcha / AntiCaptcha

Выполняет следующие шаги:

1. Отправляет CAPTCHA через эндпоинт `in.php`.
2. Периодически опрашивает `res.php` до готовности результата.
3. Возвращает текст/токен решения.

Для image CAPTCHA можно передать base64 строку (`CaptchaType.ImageBase64`) либо URL картинки (`CaptchaType.ImageUrl`). Для reCAPTCHA требуется указать `SiteKey` и `SiteUrl`. Дополнительные параметры (`action`, `min_score` и т. д.) можно передать через `AdditionalParameters`.

## Mock-режим для тестов

При установке `CAPTCHA_PROVIDER=mock` сервис не обращается к внешнему API. Это используется в unit- и интеграционных тестах, а также в CI, чтобы избежать платных запросов.

## Пример защитного использования

```csharp
if (Environment.GetEnvironmentVariable("ALLOW_CAPTCHA_IN_PROD") == "true") {
    var result = await captchaService.SolveCaptchaAsync(ct, request);
    // Используйте результат только для собственных ресурсов.
}
```

Дополнительные примеры использования доступны в модуле тестов `Core.Services.Captcha.Tests`.
