## Correção cumulativa

- Inclui toda a correção da tabela CiA 402 publicada na versão `0.2.2`.
- Atualiza automaticamente os diagnósticos de falha já gravados no histórico.
- O evento existente com código decimal `12832` passa a aparecer como `0x3220 — Subtensão no barramento CC`.
- Mantém todos os acionamentos identificados como `Delta C2000 Plus`.

## Validação

- Migração de banco validada a partir de um registro antigo com descrição incorreta.
- Processamento, consulta e persistência do caso `12832 / 0x3220` validados.
- 36 testes automatizados aprovados.
