# Changelog

## [2.4.3](https://github.com/Guizmos/Feedarr/compare/v2.4.2...v2.4.3) (2026-03-05)


### Bug Fixes

* **UI:** fix CI error ([cecc8a0](https://github.com/Guizmos/Feedarr/commit/cecc8a004a5b73353998f7f66f8623a72daad152))


### Performance Improvements

* **badges:** shared base summary cache + single-flight for /api/badges/summary ([8397e33](https://github.com/Guizmos/Feedarr/commit/8397e33f568f9591749738dd5eb389d917ca4e2c))

## [2.4.2](https://github.com/Guizmos/Feedarr/compare/v2.4.1...v2.4.2) (2026-03-05)


### Performance Improvements

* **core:** stabilize multi-client performance ([108a773](https://github.com/Guizmos/Feedarr/commit/108a773f3a228e1ed605296ace90d90746c45e2b))

## [2.4.1](https://github.com/Guizmos/Feedarr/compare/v2.4.0...v2.4.1) (2026-03-05)


### Performance Improvements

* **audit:** reduce db background load, sweep missing posters, optimize feed/top cache-miss ([17452e4](https://github.com/Guizmos/Feedarr/commit/17452e46c435f0cee010bc7cdcfc236676cefdb7))

## [2.4.0](https://github.com/Guizmos/Feedarr/compare/v2.3.0...v2.4.0) (2026-03-04)


### Features

* **maintenance:** advanced panel + provider-aware manual limits ([55f850d](https://github.com/Guizmos/Feedarr/commit/55f850d1ac2b615e5afec6155be32d08eb364417))


### Bug Fixes

* **CI:** Fix CI error ([2d8dadf](https://github.com/Guizmos/Feedarr/commit/2d8dadff536506376fd87361ab300e5a88ae7341))
* **CI:** Fix CI error ([cd38499](https://github.com/Guizmos/Feedarr/commit/cd38499d8b00d29c7ca21dee3c013d0b66b2f34e))
* **settings:** resolve undefined variables after settings refactor ([28a32c2](https://github.com/Guizmos/Feedarr/commit/28a32c2140ee2bd0ebb7517c075212a696d0a3d3))
* **UI:** fix CI error ([aa21509](https://github.com/Guizmos/Feedarr/commit/aa215091d86ef37a72901fe269ce09281f3b4c02))
* **UI:** fix CI error ([0b48d32](https://github.com/Guizmos/Feedarr/commit/0b48d3264267f536bff18865515c690f72d63c35))
* **UI:** fix error download message ([38f4d2d](https://github.com/Guizmos/Feedarr/commit/38f4d2dfa7e61c00819b6fbe8be0b668f0cc049c))


### Performance Improvements

* **posters:** enable configurable poster worker pool (1-2) ([c69e1a9](https://github.com/Guizmos/Feedarr/commit/c69e1a981194b52a5451a94647adc6b17f2acc4f))
* **posters:** move thumbnail generation out of HTTP path ([aa81486](https://github.com/Guizmos/Feedarr/commit/aa814861f48a6f0a2a586499ecac68feed6a5489))
* **providers:** add per-provider concurrency limiter ([62f3966](https://github.com/Guizmos/Feedarr/commit/62f3966e6398934d43aa8803797517083a958cd8))
* **sqlite:** remove shared cache mode and log startup options ([5763ae2](https://github.com/Guizmos/Feedarr/commit/5763ae243d1cb48b821031421c5aa1e040c22d74))
* **sync:** add bounded parallelism for source synchronization ([63c0c23](https://github.com/Guizmos/Feedarr/commit/63c0c23eaf549ba14786777557b345d60ed5a39e))

## [2.3.0](https://github.com/Guizmos/Feedarr/compare/v2.2.1...v2.3.0) (2026-03-03)


### Features

* **posters:** add webp thumbs + canonical store + refcount GC ([53ea261](https://github.com/Guizmos/Feedarr/commit/53ea2611b79ae77e0f3603fdad74d9111f817d89))
* **posters:** implement canonical store + webp thumbnails + refcount GC ([10e001b](https://github.com/Guizmos/Feedarr/commit/10e001b749e1a843766de94b9406e477a4760c14))


### Bug Fixes

* CI Action error ([1acb1fc](https://github.com/Guizmos/Feedarr/commit/1acb1fca6d1af20edfca8ffbffe6800d37caedc1))
* **password:** show point if pawssord set in settings ([f6d1bd9](https://github.com/Guizmos/Feedarr/commit/f6d1bd9e6960fc13fe09d81a8baa758d1a83abf0))
* **posters:** materialize store during worker fetch ([76c1882](https://github.com/Guizmos/Feedarr/commit/76c188247505da073bd9144043d7ef9d35d92406))
* update readme ([116ac5f](https://github.com/Guizmos/Feedarr/commit/116ac5f2b5d30cc51528c3377c9d280581927386))
* update readme FR ([49bb02e](https://github.com/Guizmos/Feedarr/commit/49bb02ec03d756bddb0683f59454deb1abba3b7b))
* update readme FR ([060b130](https://github.com/Guizmos/Feedarr/commit/060b130848c8e5c9c43ce7d60f48b4025717dc24))


### Performance Improvements

* **library:** cache posters + skeleton + priority loading ([711b930](https://github.com/Guizmos/Feedarr/commit/711b9300928a150cb1ce65a13ec5282eaa513e3c))
* **posters:** reliable queue + inflight dedupe + truthful status ([2a41a53](https://github.com/Guizmos/Feedarr/commit/2a41a5397364e3ae060400a705048bf0d149ce47))
* **sync:** batch sqlite upsert with prepared statement ([c9fe03b](https://github.com/Guizmos/Feedarr/commit/c9fe03b58be4451126603a9c5f46d9f4b7695c46))

## [2.2.1](https://github.com/Guizmos/Feedarr/compare/v2.2.0...v2.2.1) (2026-03-02)


### Bug Fixes

* **backup:** handle legacy providers.api_key during restore ([92b178a](https://github.com/Guizmos/Feedarr/commit/92b178a90692ccc21b2e8e2d8be98d872ad98c7a))


### Performance Improvements

* **stability:** async ConfigureAwait, sqlite pooling, poster fetch options, FK + migrations guards ([2710551](https://github.com/Guizmos/Feedarr/commit/2710551e4b1c86d1112d2e54251eced6000c0e71))

## [2.2.0](https://github.com/Guizmos/Feedarr/compare/v2.1.0...v2.2.0) (2026-03-02)


### Features

* enforce bounded RSS limits (backend clamp + UI validation) ([90a9c4f](https://github.com/Guizmos/Feedarr/commit/90a9c4f0838d608bc3ab9508e52c5cd9c1d66291))


### Bug Fixes

* **security:** correct same-origin detection behind reverse proxy + unblock strict mode ([26683fa](https://github.com/Guizmos/Feedarr/commit/26683fa07f01297cd709c1db80d2de3a74a3d9a2))
* **security:** harden same-origin detection behind reverse proxy and unblock strict mode ([8160d7e](https://github.com/Guizmos/Feedarr/commit/8160d7e5c80dc1038e5929720951452ff6cf542b))
* **web:** resolve eslint no-unused-vars in TopReleases ([ca04e83](https://github.com/Guizmos/Feedarr/commit/ca04e83e64eadc16da2ca909b190346f73b9cf3d))

## [2.1.0](https://github.com/Guizmos/Feedarr/compare/v2.0.0...v2.1.0) (2026-03-01)


### Features

* dynamic Top 24h + cursor pagination + perf indexes lot5 + http resilience (step 1-3 complete) ([ce7b373](https://github.com/Guizmos/Feedarr/commit/ce7b373c4ed7a1145a67f634f712ff2a0549571b))
* finalize security settings save and validation UX ([b920027](https://github.com/Guizmos/Feedarr/commit/b920027188c959ecbbcec3ee19a0a5e7be9eee7a))
* **security:** smart auth mode + bootstrap token + wizard security step ([182dc64](https://github.com/Guizmos/Feedarr/commit/182dc641482fed280601138f635562dd4197fdfe))


### Bug Fixes

* ci error ([f458da1](https://github.com/Guizmos/Feedarr/commit/f458da1677bc33ca87aa506989422c13367deabf))
* fix search result for cats ([3d66e20](https://github.com/Guizmos/Feedarr/commit/3d66e2015e4ace19585f86612cd98903ae1b5069))
* fix search result for cats bug ([efad954](https://github.com/Guizmos/Feedarr/commit/efad9542b9c59955ae1d88de78e27e7b8e5643b1))
* fix search result for cats bug + other ajustments UX ([559dd20](https://github.com/Guizmos/Feedarr/commit/559dd206c5b42bd45b2eaaa067977a3d458f20d8))
* optimisation + securisation ([1cd34df](https://github.com/Guizmos/Feedarr/commit/1cd34df873e4ba959f7e8d35bd19439a76ac20f1))
* **security:** choose protection levil in /settings ([26eaca9](https://github.com/Guizmos/Feedarr/commit/26eaca945b254fce18922b2212e4765a5f8fc3a7))
* **security:** harden poster paths + retention consistency ([469a1c0](https://github.com/Guizmos/Feedarr/commit/469a1c044cb1039e08f5ce8bc657711ade0b0956))
* **security:** prevent relock behind reverse proxy (forwarded exposed validation) ([eafebad](https://github.com/Guizmos/Feedarr/commit/eafebade9a21a8c3c0d377e07859df47768451fd))
* **security:** prevent smart auth relock + allow bootstrap token when setup required ([dfc86f9](https://github.com/Guizmos/Feedarr/commit/dfc86f9ce09952b1ae82d2cdc7f8c951b8ebd39d))


### Performance Improvements

* buffer provider stats in memory with periodic batch flush ([2f47bd0](https://github.com/Guizmos/Feedarr/commit/2f47bd02e2b5297e9d8d18a290693ca58880c64a))
* bulk sync ARR library via temp staging and short swap transaction ([f4ab14b](https://github.com/Guizmos/Feedarr/commit/f4ab14bbaa4459af428fdff96c7687d1c701fbc8))
* normalize ARR alternate titles into indexed table and use SQL lookup fallbacks ([fce8a5b](https://github.com/Guizmos/Feedarr/commit/fce8a5bb3c64f884dc765297d287da6089cdd1eb))

## [1.7.2](https://github.com/Guizmos/Feedarr/compare/v1.7.1...v1.7.2) (2026-02-24)


### Bug Fixes

* **docker:** remap feedarr user to PUID/PGID in entrypoint ([dbd180f](https://github.com/Guizmos/Feedarr/commit/dbd180f0290952ab92472156ab8badf96cc47658))

## [1.7.1](https://github.com/Guizmos/Feedarr/compare/v1.7.0...v1.7.1) (2026-02-20)


### Bug Fixes

* ajust parsing engine ([256639f](https://github.com/Guizmos/Feedarr/commit/256639f830f93ca36e41ab818eb02a38f71aeb60))
* button animation for détails modal ([371eb4a](https://github.com/Guizmos/Feedarr/commit/371eb4a7fd535bc45f3432cfe3cf84591860cb67))

## [1.7.0](https://github.com/Guizmos/Feedarr/compare/v1.6.3...v1.7.0) (2026-02-19)


### Features

* Add English language ([21ba24d](https://github.com/Guizmos/Feedarr/commit/21ba24d1b4e94f221fbae9e8088fcaa51afb2911))


### Bug Fixes

* minor design ajustment ([4415cb8](https://github.com/Guizmos/Feedarr/commit/4415cb84d8eb98922781938b308ce847622e2849))
* new badge design ([67cb7b7](https://github.com/Guizmos/Feedarr/commit/67cb7b7044b0e0dfe51c0ec44aa67bacd9d99c4d))

## [1.6.3](https://github.com/Guizmos/Feedarr/compare/v1.6.2...v1.6.3) (2026-02-18)


### Bug Fixes

* align Sonarr and request app URL placeholders ([bf745ca](https://github.com/Guizmos/Feedarr/commit/bf745cacadd4b4714cd746e62fae526bdac7fd9c))

## [1.6.2](https://github.com/Guizmos/Feedarr/compare/v1.6.1...v1.6.2) (2026-02-18)


### Bug Fixes

* Add separator behind title and cards ([59468e7](https://github.com/Guizmos/Feedarr/commit/59468e720e9bd1e3b9830a44ab07f8847545f0ce))
* Ajust serach bar ([6adf261](https://github.com/Guizmos/Feedarr/commit/6adf261d037c1028aeef5b6276476a7644174b10))
* Delete Status button in System page ([84f59c8](https://github.com/Guizmos/Feedarr/commit/84f59c8bde24aaf3359ac6a95cdc347e339d7a69))
* Opimize load() in library ([48f974b](https://github.com/Guizmos/Feedarr/commit/48f974b8ddfebb8c9629fc617807711f6903c7f2))

## [1.6.1](https://github.com/Guizmos/Feedarr/compare/v1.6.0...v1.6.1) (2026-02-18)


### Bug Fixes

* more precision for parsing engine ([17b7b11](https://github.com/Guizmos/Feedarr/commit/17b7b1133d8674d5da5cb96af260eb679b8b5fb4))

## [1.6.0](https://github.com/Guizmos/Feedarr/compare/feedarr-v1.5.0...feedarr-v1.6.0) (2026-02-18)


### Features

* GitHub releases updates + changelog UI ([1602590](https://github.com/Guizmos/Feedarr/commit/1602590c83a222f16a362752d358221f4513be50))


### Bug Fixes

* change name of github action ([0db7b27](https://github.com/Guizmos/Feedarr/commit/0db7b27667e6f040fed5524248f590739af91b4f))
* change release name ([fe8da6b](https://github.com/Guizmos/Feedarr/commit/fe8da6bf3bcd8003a50ba4f87a585f8e7a331fd9))
* delete old file ([1f68207](https://github.com/Guizmos/Feedarr/commit/1f68207e661450a4a3c37dbc266cc82da7d7c7d0))
* trigger release pipeline ([40216a0](https://github.com/Guizmos/Feedarr/commit/40216a01cd3ec89d71833a14d772623180539f49))

## [1.5.0](https://github.com/Guizmos/Feedarr/compare/feedarr-v1.4.1...feedarr-v1.5.0) (2026-02-18)


### Features

* GitHub releases updates + changelog UI ([1602590](https://github.com/Guizmos/Feedarr/commit/1602590c83a222f16a362752d358221f4513be50))
