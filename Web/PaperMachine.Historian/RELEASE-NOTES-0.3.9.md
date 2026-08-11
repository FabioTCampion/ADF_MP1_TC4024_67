# PaperMachine Historian 0.3.9

## Exclusão auditada de apontamentos de peso

- Supervisores e administradores podem excluir apontamentos de peso diretamente na tela de pesos.
- A exclusão exige uma justificativa entre 5 e 500 caracteres e registra usuário, data e hora.
- O registro original permanece preservado no banco para auditoria, incluindo o peso capturado.
- Apontamentos excluídos deixam de aparecer nas consultas, nos consolidados e na captura mais recente.
- Nova rota `DELETE /api/production/weights/{id}` protegida pela permissão `weights.delete`.

## Cadência das capturas

- Exibição do intervalo médio e do intervalo típico (mediana) entre capturas consecutivas.
- Faixa padrão calculada com tolerância de 50% da mediana, respeitando o mínimo de 15 minutos.
- Capturas com intervalo muito divergente são destacadas como **Fora do padrão**.
- O cálculo considera o período e os filtros selecionados e exige pelo menos quatro capturas para classificar divergências.

## Persistência

- Atualização automática do esquema SQLite para a versão 14.
- Inclusão dos campos de auditoria de exclusão: data/hora, usuário e justificativa.

Esta versão atualiza somente o PaperMachine Historian e não altera o programa PLC/TwinCAT.
