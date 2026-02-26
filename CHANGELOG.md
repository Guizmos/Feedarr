# Changelog

## [1.8.0](https://github.com/Guizmos/Feedarr/compare/v1.7.2...v1.8.0) (2026-02-26)


### Features

* IMPORTANT UPDATE - BREAKING CHANGE, monolithique bloc ([1f6e8be](https://github.com/Guizmos/Feedarr/commit/1f6e8be2a462bd3fd9ca622fa861a5d212080543))
* **medium-low:** Swagger, correlation ID, StatsRepository cache, ProducesResponseType ([f36b95e](https://github.com/Guizmos/Feedarr/commit/f36b95e9d97d40169a11f9b54f0b77cfa1766cfd))
* **perf:** add-response-compression — Brotli + Gzip for API and static responses ([948c4f2](https://github.com/Guizmos/Feedarr/commit/948c4f24def2ef5b3b32228f68dc5ed6755aeb5e))
* **v2:** monolith + setup lock + security P0/P1 ([34ef3a3](https://github.com/Guizmos/Feedarr/commit/34ef3a355123cf7eec8c6c524f7160381c2bfe7c))


### Bug Fixes

* **critical:** extract storage state to service, add error boundaries, prod cert guard ([0671124](https://github.com/Guizmos/Feedarr/commit/0671124ec3135ff29017d3b01e4adc7b09664394))
* CSP strict, allow img-src ([5790ff9](https://github.com/Guizmos/Feedarr/commit/5790ff9b88dee69b9e532c9600c73a54f15e19d1))
* **health:** fix-health-deep-parallel — parallel checks, per-check timeout, auth guard ([820a260](https://github.com/Guizmos/Feedarr/commit/820a26009dbe9fb6ddba773be36cb1d58065c509))
* **high:** bounded poster queue, DTO validation, deep health check ([277c8d7](https://github.com/Guizmos/Feedarr/commit/277c8d79c8f27b8a9319104bb56a73bc35bac264))
* log level rebuild ([de92f69](https://github.com/Guizmos/Feedarr/commit/de92f693c1740868bf4f3828acd4619c8236083a))
* **middleware:** fix-correlation-scope — constructor-injected logger + header sanitization ([3410aba](https://github.com/Guizmos/Feedarr/commit/3410ababe5d932bace7a833a1163ac7b8d304174))
* poster fetch bug ([03971fd](https://github.com/Guizmos/Feedarr/commit/03971fd5ba391093e1a91bb21b82590cb979c231))
* **poster:** log skip cache only when tmdb poster not saved ([d23a8db](https://github.com/Guizmos/Feedarr/commit/d23a8dbb3bbf03c2e76657841987d6cbf3e2024c))
* readme update + bug correction ([d91d816](https://github.com/Guizmos/Feedarr/commit/d91d816f7daf7c715479a85eb11f30c5b71f6287))
* **tests:** update test constructors after SystemController + StatsRepository signature changes ([717dd11](https://github.com/Guizmos/Feedarr/commit/717dd113d6a1f1ec8aaedf00f995a3ef5dbc2591))
* Top24 fix cat ([3a072de](https://github.com/Guizmos/Feedarr/commit/3a072dec5834f87c11e799d6dc6eb46f7016498f))
* **ui:** error feedback + perf stats cache + providers domain ([6a8804c](https://github.com/Guizmos/Feedarr/commit/6a8804cf39bfeccb15ff2cb12c611c73595e502c))
* **ui:** optimisation UI + bug fix + security update ([67a9e23](https://github.com/Guizmos/Feedarr/commit/67a9e239bd7fc23d1151cdcb828cdb912c32803f))
* update docs ([0925c5f](https://github.com/Guizmos/Feedarr/commit/0925c5f9a172d7bddc68fb95ae6fb6e8e08bae3a))
* **v2:** persist source category mappings + backfill legacy + canonic… ([2b10d25](https://github.com/Guizmos/Feedarr/commit/2b10d254f55748ee69f46e27d1ed6ddf114407cb))
* **v2:** persist source category mappings + backfill legacy + canonicalize top keys ([1accdab](https://github.com/Guizmos/Feedarr/commit/1accdabeb8f30b5d37cc2117cda8e76c73fe9093))


### Performance Improvements

* **filter:** cache-apierror-properties — ConcurrentDictionary&lt;Type,PropertyInfo[]&gt; cache ([8712042](https://github.com/Guizmos/Feedarr/commit/8712042ed930abf49a818926395f27050674abc9))
* **stats:** optimize-statsfeedarr-querymultiple — single QueryMultiple replaces 4 ExecuteScalar ([db94ea3](https://github.com/Guizmos/Feedarr/commit/db94ea31ed8d443933d4b9e11bf5cc5b5a893241))

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
