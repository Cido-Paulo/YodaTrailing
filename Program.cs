using Binance.Net.Clients;
using Binance.Net.Enums;
using Microsoft.Extensions.Configuration;
using System;
using YodaServerPlus;
using YodaServerPlus.Business;
using YodaServerPlus.DTO;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .Build();

Global.StrConnectionString = config.GetConnectionString("Yoda").ToString();

DailyLogger.Init(@"C:\YodaServer\TrailingProfitlog");

Console.WriteLine($"{DateTime.Now:dd/MM/yyyy HH:mm:ss} - YodaTrailing iniciado...");

while (true)
{
    DailyLogger.CheckRotate(@"C:\YodaServer\TrailingProfitlog");

    try
    {
        await ProcessarTrailingAsync();
    }
    catch (Exception ex)
    {
        var mensagem = $"{DateTime.Now:dd/MM/yyyy HH:mm:ss} - Erro ao executar o processo do trailing profit erro:: {ex.Message}";
        Console.WriteLine(mensagem);
    }

    await Task.Delay(TimeSpan.FromSeconds(30));
}


static async Task ProcessarTrailingAsync()
{
    try
    {
        List<PosicoesDTO> PosicoesPendentes = PosicaoNeg.RetornaPosicoesPendentes();

        if (PosicoesPendentes.Count == 0) return;

        //Console.WriteLine($"{DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")} tem posições para analisar...");

        //cria o ambiente
        BinanceRestClient clientUsuarioBase = BinanceTrade.CriaClient(1);

        //passa todas as posições em andamento da Cripto e verifica se não tem alguma para vender no preço
        foreach (var pos in PosicoesPendentes)
        {
            CriptoControleDTO cripto = new CriptoNeg().RetornaCripto(pos.IdCripto);
            UsuarioDTO user = new UsuarioNeg().RetornaUsuario(pos.IdUsuario);

            try
            {
                //Verifica se não tem uma ordem de venda para a Ordem de Compra, se não tiver inclui
                var result = await clientUsuarioBase.SpotApi.Trading.GetOrderAsync(
                    symbol: cripto.CodigoPar,
                    orderId: pos.OrdemVenda
                );

                if (result.Success)
                {
                    var order = result.Data;
                    if (order.Status == OrderStatus.New)
                    {
                        //busca preco atual
                        var precoAtual = BuscarPrecoAtualAsync(clientUsuarioBase, cripto.CodigoPar).Result;

                        //Executa os processos do Trailling Profit
                        await ExecutaTrailingAsync(clientUsuarioBase, order, precoAtual, pos, cripto);

                    }

                }
                else
                {
                    var mensagem = $"{DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")} Ordem de Venda: {pos.OrdemVenda} Não encontrada na Binance;";
                    Console.WriteLine(mensagem);
                }
            }
            catch (Exception ex)
            {
                var mensagem = $"{DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")} ocorreu um erro ao buscar as ordens abertas na Binance. erro: {ex.Message};";
                Console.WriteLine(mensagem);
            }
        }
    }
    catch (Exception ex)
    {
        var mensagem = $"{DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")} - Erro ao executar o processo do trailing profit erro:: {ex.Message} ";
        Console.WriteLine(mensagem);
    }
}

static async Task ExecutaTrailingAsync(BinanceRestClient clientUsuarioBase, Binance.Net.Objects.Models.Spot.BinanceOrder order, decimal precoAtual, PosicoesDTO pos, CriptoControleDTO cripto)
{
    decimal passo = 0.005m;   // 0.5%
    decimal colchao = 0.003m; // 0.3%

    var precoMargem = pos.PrecoVenda * (1 + passo);
    var novoPrecoVenda = precoAtual * (1 - colchao);

    //Console.WriteLine($"{DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")} Cripto: {cripto.CodigoPar} TP ordem: {pos.OrdemVenda} preco atual: {precoAtual}  Preco Margem: {precoMargem} novo preco de venda: {novoPrecoVenda}...");

    if (precoAtual >= precoMargem)
    {
        if (novoPrecoVenda > pos.PrecoVenda)
        {
            // cancelar ordem atual
            var result = await clientUsuarioBase.SpotApi.Trading.CancelOrderAsync(
                symbol: cripto.CodigoPar,
                orderId: pos.OrdemVenda
            );

            if (!result.Success)
            {
                var mensagem = $"{DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")} - Erro ao cancelar ordem {pos.OrdemVenda}: {result.Error}";
                Console.WriteLine(mensagem);
            }

            // 2) Ajusta para TickSize da cripto
            novoPrecoVenda = AjustarParaTickSize(novoPrecoVenda, cripto.TickSize);

            // 3) Cria nova ordem Limit mais alta
            var novaOrdem = await clientUsuarioBase.SpotApi.Trading.PlaceOrderAsync(
                symbol: cripto.CodigoPar,
                side: OrderSide.Sell,
                type: SpotOrderType.Limit,
                quantity: pos.Qtde,
                price: novoPrecoVenda,
                timeInForce: Binance.Net.Enums.TimeInForce.GoodTillCanceled,
                newClientOrderId: pos.OrdemCompra + "_venda"
            );

            // 4) Atualiza no banco
            pos.PrecoVenda = novoPrecoVenda;
            pos.OrdemVenda = novaOrdem.Data.Id;
            pos.Status = 1; //Ordem aberta ainda aguardando venda


            //Guarda a Venda no log
            string strLog = $"Trailing Profit: Valor Anterior {pos.PrecoVenda} - Novo Preço: {novoPrecoVenda} Cripto {cripto.CodigoPar} Ordem Compra: {pos.OrdemCompra} nova ordem Venda: {order.Id}.";
            Console.WriteLine(strLog);

            //Altera a Posicao informando que foi Vendida
            PosicaoNeg.AlterarPosicao(pos);
        }
    }

}

static decimal AjustarParaTickSize(decimal valor, decimal tick)
{
    return Math.Floor(valor / tick) * tick;
}


static async Task<decimal> BuscarPrecoAtualAsync(BinanceRestClient clientUsuarioBase, string strCodigoPar)
{
    //Pegar a Cotação parea Compra
    var result = await clientUsuarioBase.SpotApi.ExchangeData.GetPriceAsync(strCodigoPar);

    if (!result.Success)
        throw new Exception($"Erro ao buscar preço de {strCodigoPar}: {result.Error}");

    var preco = result.Data.Price;

    return preco;

}