## CPNTeck Paper Machine Historian 0.3.5

### Contexto produtivo e produção teórica

- calcula o formato atual pela soma de `formato 1 + formato 2 + formato 3`,
  considerando somente valores existentes e positivos;
- persiste a largura de produção no mapa e no período de receita;
- identifica a receita por produto, gramatura e formato total;
- mostra formato total, composição dos formatos e produção teórica instantânea
  no card ERP da página principal;
- usa a fórmula `largura (m) × velocidade (m/min) × gramatura (g/m²) × 60 / 1000`;
- não calcula a estimativa com ERP desatualizado, qualidade divergente, máquina
  parada, ausência de papel ou dados incompletos;
- migra o SQLite de forma incremental para o schema 10, preservando os dados
  existentes e mantendo rollback pelo instalador.

Não há alteração no PLC. A gramatura e os formatos vêm do ERP; velocidade e
presença efetiva de papel continuam vindo do ADS.
