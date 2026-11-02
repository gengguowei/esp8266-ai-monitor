#pragma once

// Bridge-rendered 240x240 RGB565 page. Keep only one row in RAM; completed
// pages live in LittleFS so returning to this mode doesn't retransmit them.
const int UP_MAX_PAGES = 32;
const int UP_CACHE_SLOTS = 2; // bounded flash use even for unusually long titles
int upRevision = -1, upPage = 0, upPages = 1;
int upCachedRev[UP_CACHE_SLOTS] = {0};
bool upChromeDrawn = false;
String upUid;
unsigned long lastUpPollMs = 0, lastUpRequestMs = 0;
const unsigned long UP_POLL_INTERVAL_MS = 5000; // local bridge cache only
File upIncoming;
int upTransferRev = -1, upTransferPage = -1, upTransferRows = 0;
uint16_t upRow[SCREEN_W];
uint8_t upRuns[SCREEN_W * 3];

int upSlot(int page) { return page % UP_CACHE_SLOTS; }
String upFileName(int page) { return String("/up") + upSlot(page) + ".raw"; }

void drawUpScreen() {
  if (upChromeDrawn) return;
  upChromeDrawn = true;
  if (upRevision > 0 && upCachedRev[upSlot(upPage)] == upRevision) {
    File file = LittleFS.open(upFileName(upPage), "r");
    if (file && file.size() == SCREEN_W * SCREEN_H * 2) {
      for (int y = 0; y < SCREEN_H; y++) {
        if (file.read((uint8_t *)upRow, sizeof(upRow)) != sizeof(upRow)) break;
        tft.pushImage(0, y, SCREEN_W, 1, upRow);
        if (y % 16 == 0) yield();
      }
      file.close();
      return;
    }
    upCachedRev[upSlot(upPage)] = 0;
  }
  tft.fillScreen(TFT_BLACK);
  tft.setTextDatum(MC_DATUM);
  tft.setTextColor(TFT_WHITE, TFT_BLACK);
  tft.drawString("BILIBILI", 120, 95, 4);
  tft.drawString("Waiting for bridge...", 120, 130, 2);
}

bool handleUpPayload(const String &payload) {
  JsonDocument doc;
  if (deserializeJson(doc, payload)) return false;
  int rev = doc["rev"] | -1, page = doc["page"] | 0, pages = doc["pages"] | 1;
  if (rev <= 0 || page < 0 || page >= UP_MAX_PAGES || pages < 1 || pages > UP_MAX_PAGES || page >= pages ||
      (doc["width"] | 0) != SCREEN_W || (doc["height"] | 0) != SCREEN_H) return false;
  String uid = doc["uid"] | "";
  if (rev != upRevision && (uid != upUid || upCachedRev[upSlot(page)] == rev)) upChromeDrawn = false;
  upUid = uid; upRevision = rev; upPage = page; upPages = pages;
  return true;
}

void beginUpTransfer(int rev) {
  if (upIncoming) upIncoming.close();
  upTransferRev = -1; upTransferRows = 0;
  if (rev != upRevision) return;
  upIncoming = LittleFS.open("/up.tmp", "w");
  if (!upIncoming) return;
  upTransferRev = rev; upTransferPage = upPage;
}

bool saveUpRow(int rev, int row, const uint8_t *bytes) {
  if (!upIncoming || rev != upTransferRev || row != upTransferRows || row >= SCREEN_H) return false;
  if (upIncoming.write(bytes, SCREEN_W * 2) != SCREEN_W * 2) {
    upIncoming.close(); upTransferRev = -1; return false;
  }
  upTransferRows++;
  return true;
}

bool endUpTransfer(int rev) {
  if (!upIncoming || rev != upTransferRev) return false;
  upIncoming.close();
  if (upTransferRows != SCREEN_H) {
    upTransferRev = -1; lastUpRequestMs = 0; LittleFS.remove("/up.tmp"); return false;
  }
  String path = upFileName(upTransferPage);
  LittleFS.remove(path);
  if (!LittleFS.rename("/up.tmp", path)) { upTransferRev = -1; return false; }
  upCachedRev[upSlot(upTransferPage)] = rev;
  if (upRevision == rev && upPage == upTransferPage) upChromeDrawn = false;
  upTransferRev = -1;
  return true;
}

void handleUpRow(const char *args) {
  int rev = -1, row = -1, consumed = 0;
  if (sscanf(args, "%d %d %n", &rev, &row, &consumed) < 2 || consumed <= 0 ||
      !upIncoming || rev != upTransferRev || row != upTransferRows) return;
  size_t count = base64Decode(args + consumed, upRuns, sizeof(upRuns));
  if (count == 0 || count % 3 != 0) return;
  int x = 0;
  uint8_t *bytes = (uint8_t *)upRow;
  for (size_t i = 0; i < count; i += 3) {
    int length = upRuns[i];
    if (length == 0 || x + length > SCREEN_W) return;
    for (int end = x + length; x < end; x++) {
      bytes[x * 2] = upRuns[i + 1]; bytes[x * 2 + 1] = upRuns[i + 2];
    }
  }
  if (x == SCREEN_W) saveUpRow(rev, row, bytes);
}

void requestUpOverSerial() {
  if (!wiredActive() || upRevision <= 0 || upCachedRev[upSlot(upPage)] == upRevision) return;
  unsigned long now = millis();
  if (lastUpRequestMs != 0 && now - lastUpRequestMs < 10000UL) return;
  lastUpRequestMs = now;
  Serial.printf("#WANTUP %d\n", upRevision);
}

void pollUp() {
  if (WiFi.status() != WL_CONNECTED || bridgeHost.length() == 0) return;
  WiFiClient client; HTTPClient http;
  http.setTimeout(BRIDGE_HTTP_TIMEOUT_MS);
  if (!http.begin(client, "http://" + bridgeHost + "/up")) return;
  int status = http.GET();
  bool valid = status == HTTP_CODE_OK && handleUpPayload(http.getString());
  http.end();
  if (!valid || upCachedRev[upSlot(upPage)] == upRevision) return;
  int revision = upRevision;
  if (!http.begin(client, "http://" + bridgeHost + "/up/screen.raw")) return;
  http.setTimeout(10000);
  if (http.GET() == HTTP_CODE_OK && http.getSize() == 4 + SCREEN_W * SCREEN_H * 2) {
    WiFiClient *stream = http.getStreamPtr();
    stream->setTimeout(3000);
    uint8_t header[4];
    if (stream->readBytes(header, 4) != 4 ||
        (((uint32_t)header[0] << 24) | ((uint32_t)header[1] << 16) | ((uint32_t)header[2] << 8) | header[3]) != (uint32_t)revision) {
      http.end(); return; // metadata/image changed between the two GETs
    }
    beginUpTransfer(revision);
    for (int row = 0; row < SCREEN_H && upIncoming; row++) {
      // readBytes has a finite timeout and returns short on disconnect.
      stream->setTimeout(3000);
      if (stream->readBytes((uint8_t *)upRow, sizeof(upRow)) != sizeof(upRow) ||
          !saveUpRow(revision, row, (uint8_t *)upRow)) break;
      yield();
    }
    endUpTransfer(revision);
  }
  http.end();
}
