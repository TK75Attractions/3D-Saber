// ボタン2つで Escape（戻る）と F12（運営モードの切替）を押す、Bluetooth（無線）キーボード。
// ボタンは「ピン ↔ GND」につなぐだけ（内部プルアップを使うので抵抗は不要）。
// USB は電源だけ（PC につながなくてよい。モバイルバッテリーでも動く）。
//
// PC の Bluetooth 設定で「Ennichi Keys」を追加（ペアリング）する。一度ペアリングすれば、電源を入れるだけで自動でつながる。
// どのマイコンでも同じファイルのまま書き込める。
//   使える   : ESP32（DevKit / WROOM-32）・ESP32-S3・ESP32-C3・ESP32-C6・ESP32-H2・Raspberry Pi Pico W / Pico 2 W
//   使えない : ESP32-S2・Arduino Uno / Nano / Leonardo / Pro Micro・Pico（W なし）… Bluetooth が無い
// 青い LED: ゆっくり点滅 = 接続待ち、消灯 = 接続中、ボタンを押すと一瞬光る。

// ───── ピン（ボタンをつなぐ場所）。違うピンにつないだらここだけ変える ─────
#if defined(CONFIG_IDF_TARGET_ESP32S3) || defined(CONFIG_IDF_TARGET_ESP32C3) || defined(CONFIG_IDF_TARGET_ESP32C6) || defined(CONFIG_IDF_TARGET_ESP32H2)
const int ESCAPE_PIN = 4, F12_PIN = 5;
#elif defined(ARDUINO_ARCH_ESP32)
const int ESCAPE_PIN = 25, F12_PIN = 26;   // ESP32 DevKit: 基板に「D25」「D26」「25」「26」などと書いてある足
#elif defined(ARDUINO_ARCH_RP2040)
const int ESCAPE_PIN = 2, F12_PIN = 3;     // Pico W: GP2 / GP3
#else
const int ESCAPE_PIN = 2, F12_PIN = 3;
#endif

const unsigned long DEBOUNCE_MS = 30;      // チャタリング（押した瞬間のガタつき）を無視する時間
const unsigned long HOLD_MS = 40;          // キーを押している時間

// ───── Bluetooth の実装をボードで選ぶ ─────
#if defined(CONFIG_IDF_TARGET_ESP32S2)
  #error "ESP32-S2 には Bluetooth がありません。ESP32 / ESP32-S3 / ESP32-C3 / ESP32-C6 / Pico W を使ってください。"
#elif defined(ARDUINO_ARCH_ESP32)
  #define KEYS_BLE 1
#elif defined(ARDUINO_ARCH_RP2040) && defined(ENABLE_BLUETOOTH)
  #define KEYS_PICO_BLE 1     // Pico W: ツール › IP/Bluetooth Stack を「IPv4 + Bluetooth」にする
#elif defined(ARDUINO_ARCH_RP2040)
  #error "Pico W で Bluetooth を使うには、ツール › IP/Bluetooth Stack を「IPv4 + Bluetooth」にしてください（W なしの Pico は Bluetooth がありません）。"
#else
  #error "このボードには Bluetooth がありません。ESP32 / ESP32-S3 / ESP32-C3 / ESP32-C6 / Pico W を使ってください。"
#endif

// HID の使用コード（USB の規格で決まっている番号）
const uint8_t HID_ESCAPE = 0x29, HID_F12 = 0x45;

#if KEYS_PICO_BLE
#include <KeyboardBLE.h>
void keysBegin() { KeyboardBLE.begin("Ennichi Keys"); }
bool keysReady() { return true; }   // 未接続のときは送っても捨てられるだけ
void keysTap(uint8_t hid) { KeyboardBLE.press(hid + 136); delay(HOLD_MS); KeyboardBLE.releaseAll(); }

#elif KEYS_BLE
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEHIDDevice.h>
#include <BLESecurity.h>

static const uint8_t REPORT_MAP[] = {
  0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, 0x01,       // Keyboard, Report ID 1
  0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02,  // 修飾キー
  0x95, 0x01, 0x75, 0x08, 0x81, 0x01,                   // 予約
  0x95, 0x06, 0x75, 0x08, 0x15, 0x00, 0x25, 0x65, 0x05, 0x07, 0x19, 0x00, 0x29, 0x65, 0x81, 0x00,  // キー6つ
  0xC0
};
BLEHIDDevice* hid = nullptr;
BLECharacteristic* input = nullptr;
volatile bool connected = false;

class Callbacks : public BLEServerCallbacks {
  void onConnect(BLEServer*) override { connected = true; }
  void onDisconnect(BLEServer* server) override {
    connected = false;
    server->getAdvertising()->start();   // 切れたらすぐ次の接続を待つ
  }
};

void keysBegin() {
  BLEDevice::init("Ennichi Keys");
  BLEServer* server = BLEDevice::createServer();
  server->setCallbacks(new Callbacks());
  hid = new BLEHIDDevice(server);
  input = hid->inputReport(1);
  hid->manufacturer()->setValue("Ennichi");
  hid->pnp(0x02, 0xE502, 0xA111, 0x0210);
  hid->hidInfo(0x00, 0x01);
  BLESecurity* security = new BLESecurity();
  security->setAuthenticationMode(ESP_LE_AUTH_REQ_SC_BOND);  // 一度ペアリングすれば次から自動でつながる
  security->setCapability(ESP_IO_CAP_NONE);                  // PIN 入力なしでペアリング（Windows / Mac とも）
  security->setInitEncryptionKey(ESP_BLE_ENC_KEY_MASK | ESP_BLE_ID_KEY_MASK);
  hid->reportMap((uint8_t*)REPORT_MAP, sizeof(REPORT_MAP));
  hid->startServices();
  hid->setBatteryLevel(100);
  BLEAdvertising* advertising = server->getAdvertising();
  advertising->setAppearance(HID_KEYBOARD);
  advertising->addServiceUUID(hid->hidService()->getUUID());
  advertising->start();
}
bool keysReady() { return connected; }
void keysTap(uint8_t code) {
  uint8_t down[8] = {0, 0, code, 0, 0, 0, 0, 0}, up[8] = {0};
  input->setValue(down, sizeof(down)); input->notify(); delay(HOLD_MS);
  input->setValue(up, sizeof(up)); input->notify();
}
#endif

// ───── ボタン ─────
struct Button {
  int pin; uint8_t hid; const char* name;
  bool down; unsigned long changedAt;
};
Button buttons[] = {
  {ESCAPE_PIN, HID_ESCAPE, "Escape", false, 0},
  {F12_PIN, HID_F12, "F12", false, 0},
};

#ifdef LED_BUILTIN
const int LED = LED_BUILTIN;
#elif defined(ARDUINO_ARCH_ESP32) && !defined(CONFIG_IDF_TARGET_ESP32S3)
const int LED = 2;   // ESP32 DevKit の青い LED
#else
const int LED = -1;
#endif

void setup() {
  Serial.begin(115200);
  for (Button& b : buttons) pinMode(b.pin, INPUT_PULLUP);
  if (LED >= 0) pinMode(LED, OUTPUT);
  keysBegin();
  Serial.println("Ennichi Keys: ready");
}

void loop() {
  unsigned long now = millis();
  for (Button& b : buttons) {
    bool pressed = digitalRead(b.pin) == LOW;   // GND につながった = 押された
    if (pressed == b.down || now - b.changedAt < DEBOUNCE_MS) continue;
    b.down = pressed; b.changedAt = now;
    if (!pressed) continue;                     // 押した瞬間だけ1回送る（押しっぱなしで連打しない）
    if (!keysReady()) { Serial.println("not connected"); continue; }
    if (LED >= 0) digitalWrite(LED, HIGH);
    keysTap(b.hid);
    if (LED >= 0) digitalWrite(LED, LOW);
    Serial.println(b.name);
  }
#if KEYS_BLE
  // Bluetooth 未接続の間は LED をゆっくり点滅させる（押した時の点灯は keysTap 中だけ）
  if (LED >= 0 && !connected) digitalWrite(LED, (now / 500) % 2);
#endif
  delay(2);
}
