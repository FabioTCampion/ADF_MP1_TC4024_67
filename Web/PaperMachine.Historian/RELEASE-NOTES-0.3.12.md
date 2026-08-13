# PaperMachine Historian 0.3.12

## Exportação dos pesos capturados

- Novo relatório PDF com resumo gerencial, pesos capturados, pesos considerados, intervalos e marcação de registros fora do padrão.
- Nova exportação Excel em formato `.xlsx`, com valores numéricos, filtros nas colunas e cabeçalho congelado.
- As duas exportações respeitam o período e a busca aplicados na tela.
- Os arquivos registram o usuário solicitante e aceitam até 5.000 pesagens por exportação.

## Consulta diária

- A página **Pesos capturados** passa a abrir com o período **Hoje** selecionado.
- Novos botões **Exportar relatório** e **Exportar Excel** no cabeçalho da página, inclusive com layout responsivo.

## Qualidade

- Testes automatizados validam a geração do PDF, a estrutura do arquivo Excel, a aplicação da busca e a inicialização das novas rotas.

Esta versão atualiza somente o PaperMachine Historian e não altera o programa PLC/TwinCAT ou a HMI.
