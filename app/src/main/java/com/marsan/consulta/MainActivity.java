package com.marsan.consulta;

import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.Context;
import android.os.Bundle;
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
        webView = new WebView(this); setContentView(webView);
        WebSettings settings = webView.getSettings();
        settings.setJavaScriptEnabled(true); settings.setDomStorageEnabled(false);
        settings.setAllowFileAccess(true); settings.setAllowContentAccess(false);
        settings.setBuiltInZoomControls(false); settings.setDisplayZoomControls(false); settings.setTextZoom(100);
        webView.setWebViewClient(new WebViewClient()); webView.setWebChromeClient(new WebChromeClient());
        webView.addJavascriptInterface(new MarsanBridge(this), "MarsanAPI");
        webView.loadUrl("file:///android_asset/index.html");
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
        private void sendError(String message) { String safe = JSONObject.quote(message); runOnUiThread(() -> webView.evaluateJavascript("window.Marsan.receiveError(" + safe + ")", null)); }
    }
    @Override public void onBackPressed() { webView.evaluateJavascript("window.Marsan.goBack()", value -> { if ("false".equals(value)) MainActivity.super.onBackPressed(); }); }
    @Override protected void onDestroy() { executor.shutdownNow(); if (webView != null) webView.destroy(); super.onDestroy(); }
}
