# LruCache em C#

Cache LRU (Least Recently Used) genérico em C# (.NET 8), com operações O(1), TTL opcional, callback de remoção, estatísticas e acesso thread-safe. A implementação usa um dicionário combinado com uma lista duplamente encadeada própria, sem depender de `LinkedList<T>`, e aceita um `TimeProvider` injetável para que a expiração seja testada de forma determinística.

## Por que este projeto

"Implemente um cache LRU" é provavelmente a questão de estrutura de dados mais clássica em entrevistas (LeetCode 146 e variações). O problema real, porém, vai além de passar nos casos de teste: em produção, caches precisam de expiração, de métricas, de segurança em concorrência e de ganchos para liberar recursos. Este projeto mostra a estrutura de dados correta e o entorno de engenharia que a torna utilizável: injeção de relógio, invariantes testadas sob concorrência e uma API pequena e previsível.

## Recursos

- `LruCache<TKey, TValue>` genérico, com `Put`, `TryGet`, `GetOrAdd`, `Remove`, `ContainsKey`, `Clear` e `PurgeExpired`.
- Get/Put/Remove em O(1) (hash map + lista encadeada por recência).
- TTL padrão por cache e TTL por entrada; leituras não renovam o TTL, escritas sim.
- Expiração preguiçosa (na leitura) mais purga explícita em O(n).
- Callback `onEvicted` com o motivo: `Capacity`, `Expired` ou `Removed`. O callback roda fora do lock.
- Contadores de `Hits`, `Misses` e `Evictions`.
- `TimeProvider` injetável (`TimeProvider.System` por padrão).

## Como executar

Pré-requisito: .NET 8 SDK. A biblioteca é consumida por referência de projeto:

```bash
dotnet build
```

```csharp
var cache = new LruCache<string, byte[]>(capacity: 1000, defaultTtl: TimeSpan.FromMinutes(5));
cache.Put("avatar:42", bytes);
if (cache.TryGet("avatar:42", out var data)) { /* hit */ }
var value = cache.GetOrAdd("avatar:43", key => LoadFromDisk(key));
```

## Como testar

```bash
dotnet test
```

Os testes cobrem ordem de remoção, atualização de recência, substituição de chaves, capacidade 1, estatísticas, callbacks, TTL com relógio manual e um teste de estresse com 8 threads que valida as invariantes da lista após 160 mil operações aleatórias.

## Decisões de projeto

- **Estrutura.** `Dictionary<TKey, Node>` dá acesso O(1) ao nó; a lista encadeada mantém a ordem de uso, com cabeça = mais recente e cauda = candidato à remoção. Mover um nó para a frente é O(1) porque cada nó guarda `Prev` e `Next`.
- **Concorrência.** Um único `lock` protege mapa e lista. É simples e correto; sob contenção muito alta, o próximo passo seria particionar o cache em *shards* (como o `MemoryCache` e o Caffeine fazem). Mesmo um `TryGet` modifica a lista, por isso um `ReaderWriterLock` não ajudaria.
- **Factory fora do lock.** `GetOrAdd` não segura o lock durante a factory, evitando deadlocks e bloqueio de outras threads. O custo é que, em corrida, a factory pode rodar mais de uma vez para a mesma chave (sem *single-flight*).
- **TTL.** O prazo é guardado como timestamp do `TimeProvider`. Entradas expiradas continuam ocupando espaço até serem lidas, expulsas por capacidade ou purgadas.
- **Callbacks fora do lock** para que código do usuário nunca execute com o lock do cache seguro.

## Estrutura de pastas

```
LruCache/
├── LruCache.sln
├── src/LruCache/           # LruCache.cs
└── tests/LruCache.Tests/   # testes xUnit e relógio manual
```

## Limitações conhecidas

- Capacidade medida em número de entradas, não em bytes.
- Sem *single-flight* em `GetOrAdd` e sem timer de purga em segundo plano.
- Lock global único; não há particionamento em shards.
- Substituir o valor de uma chave existente não dispara o callback de remoção.

## Licença

MIT. Veja o arquivo [LICENSE](LICENSE).
