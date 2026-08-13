# PaperMachine Historian 0.3.13

## Exportação Excel das métricas da máquina

- Novo botão **Exportar para Excel** na página **Métricas da máquina**, mantendo o mesmo período e a mesma regra de velocidade produtiva usados na tela e no relatório PDF.
- A pasta de trabalho possui as abas **Resumo**, **Desempenho horário**, **Quebras** e **Pareto**.
- Datas, percentuais, velocidades, durações e quantidades são gravados como valores próprios do Excel, com filtros e cabeçalhos congelados.

## Compatibilidade da exportação de pesos

- A geração do arquivo `.xlsx` de pesos capturados agora usa o SDK oficial Open XML, corrigindo a incompatibilidade que impedia a abertura do arquivo no Microsoft Excel.
- Os botões de exportação da página **Pesos capturados** foram padronizados com o estilo visual da página **Métricas da máquina**.

## Qualidade

- Validação estrutural dos arquivos Excel para Office 2019 ou superior.
- Testes automatizados, lint e build de produção executados com sucesso.

Esta versão atualiza somente o PaperMachine Historian e não altera o programa PLC/TwinCAT ou a HMI.
