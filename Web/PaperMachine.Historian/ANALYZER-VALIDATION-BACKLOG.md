# Backlog de validação — Melhores condições

Este backlog define o que deve ser validado com operação antes do próximo
lançamento do analisador. A produção comparável é identificada por produto e
gramatura; a largura não participa da comparação.

## P0 — Aquisição e confiabilidade dos sinais

- [x] Usar `paperMachineHmiCommands.mixPumpRatio` como fonte oficial do ratio.
- [x] Registrar o baseline de `mixPumpRatio` na primeira leitura após iniciar o serviço.
- [x] Amostrar o comando junto da telemetria e manter os eventos de alteração para auditoria.
- [x] Desconsiderar ratio fora da faixa configurada no PLC (`0,5–2,0`).
- [ ] Comparar por 1 turno o ratio do Historian com o valor exibido na HMI.
- [ ] Confirmar a unidade de `stockPumpSpeed` com automação: `%`, `Hz` ou `rpm`.
- [ ] Confirmar que todos os torques utilizados estão normalizados em `%`.
- [ ] Confirmar se `dryingSectionGroup1/2/3SteamPressure` representa pressão real ou referência.
- [ ] Medir cobertura por sinal durante uma produção completa; meta mínima: 95% dos minutos produtivos.
- [ ] Exibir motivo para ausência de dado: não coletado, inválido ou equipamento parado.

## P0 — Segmentação das produções

- [x] Comparar apenas mesmo produto e mesma gramatura.
- [x] Ignorar largura na formação dos grupos comparáveis.
- [x] Interromper uma sequência quando houver quebra, perda de papel ou lacuna de telemetria.
- [ ] Validar com operação pelo menos 10 sequências conhecidas, incluindo início, fim e duração sem quebra.
- [ ] Confirmar se 10 m/min é o limite adequado para considerar a máquina em produção.
- [ ] Confirmar se 30 minutos é a duração mínima adequada para uma sequência candidata.

## P1 — Parâmetros e cálculos

- [x] Limitar o catálogo às 11 famílias aprovadas, totalizando 17 séries.
- [x] Calcular torque médio de cada grupo de secagem usando somente acionamentos ativos.
- [ ] Validar o limite de 10 m/min usado para identificar um acionamento ativo.
- [ ] Comparar o torque calculado de G1, G2 e G3 com uma conferência manual de pelo menos 3 períodos.
- [ ] Decidir com operação entre média e mediana como valor representativo de cada sinal.
- [ ] Decidir entre mínimo/máximo e percentis P10–P90 como faixa recomendada.
- [ ] Definir cobertura mínima individual para permitir que um parâmetro apareça como recomendação.
- [ ] Confirmar se valores durante partida e parada devem ser descartados além do filtro atual de produção.

## P1 — Ranking e apresentação

- [ ] Validar os pesos dos perfis Equilíbrio, Estabilidade e Velocidade.
- [ ] Conferir se a produção apontada como melhor faz sentido para operador e processo.
- [ ] Mostrar rastreabilidade da recomendação: ordem, início, fim, duração e cobertura.
- [ ] Mostrar valor atual, valor representativo histórico e faixa do período selecionado.
- [ ] Não classificar ausência de dado ou zero inválido como condição operacional.
- [ ] Revisar nomes e unidades das 17 séries na interface com operação.

## P2 — Histórico anterior ao ajuste

- [ ] Avaliar recuperação do ratio antigo por `velocidade do jato × 60 / velocidade da tela`.
- [ ] Comparar o ratio derivado com o comando real durante um turno antes de executar backfill.
- [ ] Criar backfill idempotente somente se o erro do ratio derivado estiver dentro da tolerância aprovada.
- [ ] Reprocessar snapshots antigos para as novas colunas agregadas quando houver fonte confiável.
- [ ] Gerar relatório de quantidade recuperada, rejeitada e sem fonte disponível.

## Roteiro de validação operacional

1. Selecionar pelo menos 3 combinações de produto e gramatura.
2. Separar pelo menos 3 campanhas conhecidas para cada combinação.
3. Conferir manualmente duração sem quebra, velocidade média e os 11 grupos de parâmetros.
4. Registrar divergências entre HMI, Historian e cálculo do analisador.
5. Ajustar limites e estatística somente com evidência registrada.
6. Aprovar a interface com produção antes de publicar a próxima versão estável.

## Critério de liberação

O analisador estará pronto para lançamento quando todos os itens P0 estiverem
concluídos, não houver divergência de unidade, a cobertura mínima for atendida e
as melhores campanhas forem aprovadas pela operação em pelo menos 80% dos casos
do roteiro de validação.
