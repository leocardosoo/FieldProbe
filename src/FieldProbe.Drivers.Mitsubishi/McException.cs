namespace FieldProbe.Drivers.Mitsubishi;

/// <summary>O CLP respondeu com código de término (end code) diferente de zero.</summary>
public sealed class McException : Exception
{
    public McException(ushort endCode, string context)
        : base($"O CLP recusou o pedido ({context}): código 0x{endCode:X4} · {Describe(endCode)}")
    {
        EndCode = endCode;
    }

    public ushort EndCode { get; }

    /// <summary>Explicação dos códigos de erro mais comuns do MC Protocol (Q/L, iQ-R e iQ-F).</summary>
    public static string Describe(ushort code) => code switch
    {
        0x0000 => "sem erro",
        0x0055 => "escrita com a CPU em RUN não permitida: no GX Works habilite a escrita em RUN nas configurações Ethernet da CPU",
        0xC050 => "o CLP está configurado para código ASCII: mude o 'Communication Data Code' da porta para Binário",
        0xC051 or 0xC052 or 0xC053 or 0xC054 => "quantidade de pontos fora do limite permitido por pedido",
        0xC056 => "endereço além do fim do dispositivo (o CLP tem menos pontos desse tipo do que o pedido)",
        0xC058 => "tamanho dos dados não confere com o pedido",
        0xC059 => "comando não suportado por esta CPU ou módulo (confira a série escolhida na conexão)",
        0xC05B => "este dispositivo não pode ser lido ou escrito por MC Protocol",
        0xC05C => "pedido com conteúdo inválido (por exemplo, acesso em bit a um dispositivo de palavra)",
        0xC05F => "o pedido não pode ser executado nesta CPU",
        0xC060 => "pedido com conteúdo inválido (especificação de bit)",
        0xC061 => "tamanho do pedido não confere com o número de pontos",
        0xC0B5 => "a CPU não consegue tratar os dados pedidos",
        0xC200 or 0xC201 or 0xC204 => "porta protegida por senha remota (remote password): desbloqueie no GX Works ou use outra porta",
        0x4030 or 0x4031 => "dispositivo fora da faixa configurada nos parâmetros da CPU",
        0x4040 or 0x4041 or 0x4042 or 0x4043 => "erro no módulo ou na CPU de destino (confira rede/estação)",
        _ => "consulte a tabela de códigos de erro do manual do MC Protocol (SLMP) da sua CPU"
    };
}
