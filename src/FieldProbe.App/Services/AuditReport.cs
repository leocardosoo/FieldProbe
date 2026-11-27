using System.Net;
using System.Text;
using FieldProbe.Core;

namespace FieldProbe.App.Services;

/// <summary>Diagnóstico de uma conexão do projeto para o relatório.</summary>
public sealed record ConnectionAudit(string Name, string Protocol, string Endpoint, IReadOnlyList<DiagnosticItem> Items, string? Note);

/// <summary>
/// Relatório de auditoria de segurança OT em HTML autocontido (abre em qualquer navegador e
/// vira PDF com Ctrl+P → "Salvar como PDF").
/// </summary>
public static class AuditReport
{
    public static string Build(string projectName, IReadOnlyList<ScanResult> scan, DateTime? scanTime, IReadOnlyList<ConnectionAudit> connections)
    {
        var now = DateTime.Now;
        var scanFindings = scan.SelectMany(r => r.Findings.Select(f => (Asset: r, Finding: f))).ToList();
        var diagItems = connections.SelectMany(c => c.Items.Select(i => (Connection: c, Item: i))).ToList();

        var critical = scanFindings.Count(x => x.Finding.Severity == DiagnosticSeverity.Critical)
                       + diagItems.Count(x => x.Item.Severity == DiagnosticSeverity.Critical);
        var warning = scanFindings.Count(x => x.Finding.Severity == DiagnosticSeverity.Warning)
                      + diagItems.Count(x => x.Item.Severity == DiagnosticSeverity.Warning);
        var score = Math.Max(0, 100 - critical * 20 - warning * 5);
        var (rating, ratingClass) = score >= 80 ? ("Bom", "ok") : score >= 50 ? ("Requer atenção", "warn") : ("Crítico", "crit");

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\">");
        sb.Append("<title>Relatório de auditoria OT · ").Append(E(projectName)).Append("</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body>");

        // Cabeçalho
        sb.Append("<header><div><div class=\"brand\">FieldProbe</div><h1>Relatório de auditoria de segurança OT</h1>");
        sb.Append("<div class=\"meta\">Projeto: <b>").Append(E(projectName)).Append("</b> · Gerado em ")
          .Append(now.ToString("dd/MM/yyyy HH:mm")).Append(" · Analista: ").Append(E(Environment.UserName))
          .Append(" · Estação: ").Append(E(Environment.MachineName)).Append("</div></div>");
        sb.Append("<div class=\"score ").Append(ratingClass).Append("\"><div class=\"num\">").Append(score)
          .Append("</div><div>").Append(rating).Append("</div><small>nota de exposição (0–100)</small></div></header>");

        // Resumo
        sb.Append("<section><h2>1. Resumo</h2><div class=\"cards\">");
        Card(sb, scan.Count.ToString(), "ativos encontrados na varredura");
        Card(sb, connections.Count.ToString(), "conexões analisadas");
        Card(sb, critical.ToString(), "achados críticos", critical > 0 ? "crit" : "");
        Card(sb, warning.ToString(), "pontos de atenção", warning > 0 ? "warn" : "");
        sb.Append("</div>");
        sb.Append("<p>").Append(Summary(scan, critical, warning)).Append("</p></section>");

        // Principais recomendações
        var recommendations = scanFindings
            .Where(x => x.Finding.Severity is DiagnosticSeverity.Critical or DiagnosticSeverity.Warning && x.Finding.Recommendation.Length > 0)
            .GroupBy(x => x.Finding.Recommendation)
            .Select(g => (Recommendation: g.Key, Severity: g.Max(x => x.Finding.Severity), Assets: g.Select(x => x.Asset.Ip).Distinct().ToList()))
            .OrderByDescending(x => x.Severity).ThenByDescending(x => x.Assets.Count)
            .ToList();

        sb.Append("<section><h2>2. Principais recomendações</h2>");
        if (recommendations.Count == 0)
        {
            sb.Append("<p class=\"muted\">Nenhuma recomendação gerada pela varredura. Veja também o diagnóstico das conexões.</p>");
        }
        else
        {
            sb.Append("<ol class=\"recs\">");
            foreach (var (recommendation, severity, assets) in recommendations)
            {
                sb.Append("<li><span class=\"tag ").Append(Class(severity)).Append("\">").Append(Label(severity)).Append("</span> ")
                  .Append(E(recommendation)).Append("<div class=\"muted\">Ativos: ").Append(E(string.Join(", ", assets))).Append("</div></li>");
            }
            sb.Append("</ol>");
        }
        sb.Append("</section>");

        // Inventário
        sb.Append("<section><h2>3. Inventário de ativos</h2>");
        if (scan.Count == 0)
        {
            sb.Append("<p class=\"muted\">Nenhuma varredura de rede incluída neste relatório.</p>");
        }
        else
        {
            sb.Append("<p class=\"muted\">Varredura realizada em ").Append(scanTime?.ToString("dd/MM/yyyy HH:mm") ?? "-").Append(".</p>");
            sb.Append("<table><thead><tr><th>Risco</th><th>IP</th><th>Hostname</th><th>MAC</th><th>Protocolos</th><th>Identificação</th><th>Portas</th></tr></thead><tbody>");
            foreach (var r in scan.OrderByDescending(r => r.WorstSeverity).ThenBy(r => r.Ip, StringComparer.Ordinal))
            {
                sb.Append("<tr><td><span class=\"tag ").Append(Class(r.WorstSeverity)).Append("\">").Append(r.SeverityText).Append("</span></td>")
                  .Append("<td class=\"mono\"><b>").Append(E(r.Ip)).Append("</b></td>")
                  .Append("<td>").Append(E(r.Hostname)).Append("</td>")
                  .Append("<td class=\"mono\">").Append(E(r.Mac)).Append("</td>")
                  .Append("<td>").Append(E(r.ProtocolsText)).Append("</td>")
                  .Append("<td>").Append(E(r.IdentityText)).Append("</td>")
                  .Append("<td class=\"mono\">").Append(E(r.PortsText)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // Achados da varredura
        sb.Append("<section><h2>4. Achados da varredura</h2>");
        var relevant = scanFindings.Where(x => x.Finding.Severity != DiagnosticSeverity.Ok)
            .OrderByDescending(x => x.Finding.Severity).ThenBy(x => x.Asset.Ip, StringComparer.Ordinal).ToList();
        if (relevant.Count == 0)
        {
            sb.Append("<p class=\"muted\">Sem achados.</p>");
        }
        else
        {
            sb.Append("<table><thead><tr><th>Severidade</th><th>Ativo</th><th>Categoria</th><th>Achado</th><th>Recomendação</th></tr></thead><tbody>");
            foreach (var (asset, finding) in relevant)
            {
                sb.Append("<tr><td><span class=\"tag ").Append(Class(finding.Severity)).Append("\">").Append(Label(finding.Severity)).Append("</span></td>")
                  .Append("<td class=\"mono\">").Append(E(asset.Ip)).Append("</td>")
                  .Append("<td>").Append(E(finding.Category)).Append("</td>")
                  .Append("<td>").Append(E(finding.Message)).Append("</td>")
                  .Append("<td>").Append(E(finding.Recommendation)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // Diagnóstico das conexões
        sb.Append("<section><h2>5. Diagnóstico das conexões do projeto</h2>");
        if (connections.Count == 0)
        {
            sb.Append("<p class=\"muted\">Nenhuma conexão no projeto.</p>");
        }
        foreach (var c in connections)
        {
            sb.Append("<h3>").Append(E(c.Name)).Append(" <span class=\"muted\">· ").Append(E(c.Protocol)).Append(" · ").Append(E(c.Endpoint)).Append("</span></h3>");
            if (c.Note is not null) sb.Append("<p class=\"muted\">").Append(E(c.Note)).Append("</p>");
            if (c.Items.Count == 0) continue;

            sb.Append("<table><thead><tr><th>Resultado</th><th>Categoria</th><th>Detalhe</th></tr></thead><tbody>");
            foreach (var item in c.Items)
            {
                sb.Append("<tr><td><span class=\"tag ").Append(Class(item.Severity)).Append("\">").Append(Label(item.Severity)).Append("</span></td>")
                  .Append("<td>").Append(E(item.Category)).Append("</td>")
                  .Append("<td>").Append(E(item.Message)).Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section>");

        // Boas práticas
        sb.Append("<section><h2>6. Boas práticas gerais</h2><ul>");
        foreach (var practice in GoodPractices) sb.Append("<li>").Append(E(practice)).Append("</li>");
        sb.Append("</ul></section>");

        // Metodologia
        sb.Append("<section><h2>7. Metodologia e limitações</h2><p class=\"muted\">");
        sb.Append("A varredura abre conexões TCP nas portas selecionadas e executa apenas handshakes de identificação ");
        sb.Append("(Modbus FC 43, EtherNet/IP ListIdentity, COTP do S7, HEL/ACK do OPC UA, CONNECT anônimo do MQTT, handshake FINS/TCP com modelo e modo da CPU Omron e modelo da CPU Mitsubishi pelo MC Protocol). ");
        sb.Append("Nenhuma variável de processo foi escrita e nenhuma vulnerabilidade foi explorada. O diagnóstico das conexões usa ");
        sb.Append("sessões autorizadas configuradas no projeto. Este relatório é um levantamento técnico e não substitui uma avaliação ");
        sb.Append("de risco completa (ex.: IEC 62443-3-2), testes de intrusão ou a análise das políticas e procedimentos da planta.");
        sb.Append("</p></section>");

        sb.Append("<footer>Gerado pelo FieldProbe ").Append(E(AppInfo.Version)).Append(" · ").Append(now.ToString("dd/MM/yyyy HH:mm:ss"))
          .Append(" · Para salvar em PDF: Ctrl+P → Salvar como PDF.</footer>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static readonly string[] GoodPractices =
    {
        "Manter o inventário de ativos OT atualizado (IP, modelo, firmware, responsável) e revisá-lo periodicamente.",
        "Segmentar a rede em zonas e conduítes (conceito da IEC 62443-3-2/3-3), separando a rede OT da rede corporativa por firewall.",
        "Liberar protocolos industriais sem autenticação (Modbus, S7, EtherNet/IP, FINS) somente entre os ativos que realmente precisam se comunicar.",
        "Centralizar o acesso remoto em jump server ou VPN com autenticação multifator, sem acessos diretos a CLPs e IHMs.",
        "Trocar senhas padrão de fábrica e desabilitar serviços não usados (Telnet, FTP, HTTP, VNC sem senha).",
        "Manter backups versionados dos programas de CLP, IHM e configurações de rede, e testar a restauração.",
        "Planejar atualizações de firmware e patches com janela de manutenção, conforme avisos dos fabricantes.",
        "Monitorar a rede OT de forma passiva para detectar ativos novos e comunicações fora do padrão."
    };

    private static string Summary(IReadOnlyList<ScanResult> scan, int critical, int warning)
    {
        if (critical == 0 && warning == 0)
            return "Não foram encontrados achados críticos ou pontos de atenção no escopo analisado.";

        var protocols = scan.SelectMany(r => r.Protocols).Distinct().Take(8).ToList();
        var text = $"Foram identificados {critical} achado(s) crítico(s) e {warning} ponto(s) de atenção.";
        if (protocols.Count > 0)
            text += $" Protocolos observados na rede: {string.Join(", ", protocols)}.";
        text += " Priorize os itens críticos da seção 2.";
        return E(text);
    }

    private static void Card(StringBuilder sb, string value, string label, string cssClass = "")
    {
        sb.Append("<div class=\"card ").Append(cssClass).Append("\"><div class=\"num\">").Append(E(value))
          .Append("</div><div class=\"muted\">").Append(E(label)).Append("</div></div>");
    }

    private static string Class(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Critical => "crit",
        DiagnosticSeverity.Warning => "warn",
        DiagnosticSeverity.Ok => "ok",
        _ => "info"
    };

    private static string Label(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Critical => "Crítico",
        DiagnosticSeverity.Warning => "Atenção",
        DiagnosticSeverity.Ok => "OK",
        _ => "Info"
    };

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private const string Css = """
        *{box-sizing:border-box}body{font-family:"Segoe UI",Arial,sans-serif;color:#1f2328;margin:0;padding:32px 40px;font-size:13px;line-height:1.45}
        header{display:flex;justify-content:space-between;align-items:center;border-bottom:3px solid #0f6cbd;padding-bottom:16px;margin-bottom:8px}
        .brand{color:#0f6cbd;font-weight:700;letter-spacing:.5px}h1{margin:2px 0 6px;font-size:24px}
        .meta{color:#57606a;font-size:12px}h2{font-size:17px;margin:28px 0 10px;color:#0f6cbd}h3{font-size:14px;margin:18px 0 6px}
        .score{text-align:center;border-radius:10px;padding:10px 18px;min-width:150px}.score .num{font-size:34px;font-weight:700;line-height:1}
        .score small{display:block;color:#57606a;font-size:10px;margin-top:2px}
        .score.ok{background:#dafbe1;color:#1a7f37}.score.warn{background:#fff8c5;color:#9a6700}.score.crit{background:#ffebe9;color:#cf222e}
        .cards{display:flex;gap:12px;flex-wrap:wrap}.card{flex:1;min-width:150px;border:1px solid #dde1e6;border-radius:8px;padding:12px 14px}
        .card .num{font-size:26px;font-weight:700}.card.crit .num{color:#cf222e}.card.warn .num{color:#9a6700}
        table{width:100%;border-collapse:collapse;margin-top:6px}th{background:#f6f8fa;color:#57606a;text-align:left;font-weight:600;padding:7px 8px;border-bottom:1px solid #dde1e6}
        td{padding:6px 8px;border-bottom:1px solid #eef0f3;vertical-align:top}tr{page-break-inside:avoid}
        .mono{font-family:Consolas,monospace}.muted{color:#57606a}
        .tag{display:inline-block;border-radius:10px;padding:1px 8px;font-size:11px;font-weight:600;white-space:nowrap}
        .tag.crit{background:#ffebe9;color:#cf222e}.tag.warn{background:#fff8c5;color:#9a6700}.tag.ok{background:#dafbe1;color:#1a7f37}.tag.info{background:#ddf4ff;color:#0969da}
        ol.recs li{margin-bottom:8px}ul li{margin-bottom:4px}
        footer{margin-top:36px;padding-top:10px;border-top:1px solid #dde1e6;color:#8c959f;font-size:11px}
        @media print{body{padding:0}@page{margin:16mm 14mm}h2{page-break-after:avoid}}
        """;
}
