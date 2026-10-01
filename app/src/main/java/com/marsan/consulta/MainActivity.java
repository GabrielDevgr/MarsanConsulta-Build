package com.marsan.consulta;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.Context;
import android.os.Bundle;
import android.view.View;
import android.view.WindowManager;
import android.print.PrintAttributes;
import android.print.PrintDocumentAdapter;
import android.print.PrintManager;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import org.json.JSONObject;
import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class MainActivity extends Activity {
    private WebView webView;
    private final ExecutorService executor = Executors.newSingleThreadExecutor();

    @SuppressLint({"SetJavaScriptEnabled", "JavascriptInterface"})
    @Override protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        enableFullscreen();
        webView = new WebView(this); setContentView(webView);
        WebSettings settings = webView.getSettings();
        settings.setJavaScriptEnabled(true); settings.setDomStorageEnabled(false);
        settings.setAllowFileAccess(true); settings.setAllowContentAccess(false);
        settings.setBuiltInZoomControls(false); settings.setDisplayZoomControls(false); settings.setTextZoom(100);
        webView.setWebViewClient(new WebViewClient()); webView.setWebChromeClient(new WebChromeClient());
        webView.addJavascriptInterface(new MarsanBridge(this), "MarsanAPI");
        webView.loadUrl("file:///android_asset/index.html");
    }

    private void enableFullscreen() {
        getWindow().setFlags(
            WindowManager.LayoutParams.FLAG_FULLSCREEN,
            WindowManager.LayoutParams.FLAG_FULLSCREEN
        );
        getWindow().getDecorView().setSystemUiVisibility(
            View.SYSTEM_UI_FLAG_FULLSCREEN |
            View.SYSTEM_UI_FLAG_HIDE_NAVIGATION |
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY |
            View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN |
            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION |
            View.SYSTEM_UI_FLAG_LAYOUT_STABLE
        );
    }

    @Override public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) enableFullscreen();
    }

    private final class MarsanBridge {
        private final Context context; MarsanBridge(Context context) { this.context = context; }
        @JavascriptInterface public void loadData() {
            executor.execute(() -> {
                try {
                    URL url = new URL(ApiConfig.API_URL); HttpURLConnection connection = (HttpURLConnection) url.openConnection();
                    connection.setRequestMethod("GET"); connection.setConnectTimeout(15000); connection.setReadTimeout(25000);
                    connection.setRequestProperty("Accept", "application/json"); connection.setRequestProperty("x-marsan-consulta-key", ApiConfig.API_KEY);
                    int status = connection.getResponseCode();
                    BufferedReader reader = new BufferedReader(new InputStreamReader(status >= 200 && status < 300 ? connection.getInputStream() : connection.getErrorStream(), StandardCharsets.UTF_8));
                    StringBuilder body = new StringBuilder(); String line; while ((line = reader.readLine()) != null) body.append(line); reader.close(); connection.disconnect();
                    if (status < 200 || status >= 300) { String message = "Falha ao consultar dados (HTTP " + status + ")"; try { String apiMessage = new JSONObject(body.toString()).optString("error"); if (!apiMessage.isEmpty()) message = apiMessage; } catch (Exception ignored) {} sendError(message); return; }
                    String payload = JSONObject.quote(body.toString()); runOnUiThread(() -> webView.evaluateJavascript("window.Marsan.receiveData(" + payload + ")", null));
                } catch (Exception error) { sendError("Não foi possível conectar ao Marsan. Verifique a internet e tente novamente."); }
            });
        }
        @JavascriptInterface public void printCurrentView() {
            runOnUiThread(() -> { PrintManager printManager = (PrintManager) context.getSystemService(Context.PRINT_SERVICE); PrintDocumentAdapter adapter = webView.createPrintDocumentAdapter("Marsan Consulta"); PrintAttributes attributes = new PrintAttributes.Builder().setMediaSize(PrintAttributes.MediaSize.ISO_A4.asLandscape()).setColorMode(PrintAttributes.COLOR_MODE_COLOR).setMinMargins(PrintAttributes.Margins.NO_MARGINS).build(); printManager.print("Marsan Consulta", adapter, attributes); });
        }
        @JavascriptInterface public void requestRemotePrint(String customerName, String title, String html) {
            executor.execute(() -> {
                HttpURLConnection connection = null;
                try {
                    URL url = new URL(ApiConfig.PRINT_URL);
                    connection = (HttpURLConnection) url.openConnection();
                    connection.setRequestMethod("POST");
                    connection.setConnectTimeout(15000);
                    connection.setReadTimeout(30000);
                    connection.setDoOutput(true);
                    connection.setRequestProperty("Accept", "application/json");
                    connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                    connection.setRequestProperty("x-marsan-consulta-key", ApiConfig.API_KEY);

                    JSONObject payload = new JSONObject();
                    payload.put("customerName", customerName);
                    payload.put("title", title);
                    payload.put("documentType", "controle_saldo");
                    payload.put("documentFormat", "html");
                    payload.put("documentContent", html);
                    payload.put("copies", 1);
                    payload.put("requestedBy", "Marsan Consulta");

                    byte[] bytes = payload.toString().getBytes(StandardCharsets.UTF_8);
                    connection.setFixedLengthStreamingMode(bytes.length);
                    try (OutputStream output = connection.getOutputStream()) { output.write(bytes); }

                    int status = connection.getResponseCode();
                    BufferedReader reader = new BufferedReader(new InputStreamReader(
                        status >= 200 && status < 300 ? connection.getInputStream() : connection.getErrorStream(),
                        StandardCharsets.UTF_8
                    ));
                    StringBuilder body = new StringBuilder();
                    String line;
                    while ((line = reader.readLine()) != null) body.append(line);
                    reader.close();

                    if (status >= 200 && status < 300) {
                        sendRemotePrintResult(true, "Enviado para a Marsan.");
                    } else {
                        String message = "Falha ao enviar para impressão (HTTP " + status + ")";
                        try {
                            String apiMessage = new JSONObject(body.toString()).optString("error");
                            if (!apiMessage.isEmpty()) message = apiMessage;
                        } catch (Exception ignored) {}
                        sendRemotePrintResult(false, message);
                    }
                } catch (Exception error) {
                    sendRemotePrintResult(false, "Não foi possível enviar a impressão. Verifique a internet.");
                } finally {
                    if (connection != null) connection.disconnect();
                }
            });
        }

        private void sendRemotePrintResult(boolean ok, String message) {
            String safe = JSONObject.quote(message);
            runOnUiThread(() -> webView.evaluateJavascript(
                "window.Marsan.receiveRemotePrint(" + (ok ? "true" : "false") + "," + safe + ")", null
            ));
        }
        private void sendError(String message) { String safe = JSONObject.quote(message); runOnUiThread(() -> webView.evaluateJavascript("window.Marsan.receiveError(" + safe + ")", null)); }
    }
    @Override public void onBackPressed() { webView.evaluateJavascript("window.Marsan.goBack()", value -> { if ("false".equals(value)) MainActivity.super.onBackPressed(); }); }
    @Override protected void onDestroy() { executor.shutdownNow(); if (webView != null) webView.destroy(); super.onDestroy(); }
}
