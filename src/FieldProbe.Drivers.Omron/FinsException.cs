namespace FieldProbe.Drivers.Omron;

/// <summary>O CLP respondeu com código de término (end code) FINS diferente de zero.</summary>
public sealed class FinsException : Exception
{
    public FinsException(ushort endCode, string context)
        : base($"O CLP recusou o pedido ({context}): código 0x{endCode:X4} · {Describe(endCode)}")
    {
        EndCode = endCode;
    }

    public ushort EndCode { get; }

    /// <summary>
    /// Separa o código real dos sinalizadores: no 1º byte o bit 7 indica erro de repasse na rede;
    /// no 2º byte os bits 6 e 7 só avisam que a CPU tem erro (não fatal / fatal), mesmo com o pedido OK.
    /// </summary>
    public static (ushort Code, bool RelayError, bool NonFatal, bool Fatal) Split(byte mres, byte sres) =>
        ((ushort)(((mres & 0x7F) << 8) | (sres & 0x3F)), (mres & 0x80) != 0, (sres & 0x40) != 0, (sres & 0x80) != 0);

    /// <summary>Explicação dos códigos de término FINS mais comuns.</summary>
    public static string Describe(ushort code) => code switch
    {
        0x0000 => "sem erro",
        0x0001 => "serviço cancelado",
        0x0101 or 0x0102 or 0x0103 => "o nó local não está na rede (confira o número de rede/nó)",
        0x0105 => "número de nó repetido ou inválido na rede",
        0x0201 or 0x0202 => "o nó de destino não está na rede ou não respondeu (confira o nó do CLP)",
        0x0204 => "o nó de destino está ocupado",
        0x0205 => "o CLP não respondeu a tempo",
        0x0301 or 0x0302 => "erro no módulo de comunicação ou na CPU",
        0x0401 => "comando FINS não definido",
        0x0402 => "comando não suportado por este modelo/versão",
        0x0501 or 0x0502 or 0x0503 or 0x0504 => "erro de roteamento entre redes (tabela de roteamento)",
        0x1001 => "pedido longo demais",
        0x1002 => "pedido curto demais",
        0x1003 => "quantidade de elementos não confere com os dados",
        0x1004 => "formato do comando inválido",
        0x1005 => "erro no cabeçalho do pedido",
        0x1101 => "área de memória inexistente neste CLP (código de área inválido)",
        0x1103 => "endereço fora da faixa da área",
        0x1104 => "o pedido passa do fim da área",
        0x110B => "resposta longa demais: peça menos palavras por vez",
        0x110C => "parâmetro inválido",
        0x2002 => "área protegida",
        0x2101 => "área somente leitura",
        0x2102 or 0x2103 => "área protegida contra escrita",
        0x2201 or 0x2202 => "não é possível executar no modo atual da CPU (ex.: escrita bloqueada em RUN)",
        0x2205 => "a CPU está em modo de monitoração/servidor ocupado",
        0x2301 or 0x2302 => "unidade de memória (cartão) ausente ou com erro",
        0x2601 or 0x2602 => "área de memória sem acesso (protegida ou já em uso)",
        _ => "consulte a tabela de códigos de término do manual de comandos FINS (W342)"
    };

    /// <summary>Erros do cabeçalho FINS/TCP (campo "error code" do handshake).</summary>
    public static string DescribeTcp(uint code) => code switch
    {
        0x01 => "o cabeçalho não é FINS",
        0x02 => "tamanho de dados longo demais",
        0x03 => "comando FINS/TCP não suportado",
        0x20 => "todas as conexões FINS/TCP do CLP estão em uso",
        0x21 => "o nó escolhido já está conectado",
        0x22 => "acesso a nó protegido de um IP não autorizado (tabela de IPs da porta FINS)",
        0x23 => "nó do cliente fora da faixa",
        0x24 => "o mesmo nó já é usado por outro cliente",
        0x25 => "todos os números de nó estão em uso",
        _ => $"erro FINS/TCP 0x{code:X}"
    };
}
