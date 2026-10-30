// ボタン2つで Escape（戻る）と F12（運営モードの切替）を押すキーボード。
// ボタンは「ピン ↔ GND」につなぐだけ（内部プルアップを使うので抵抗は不要）。
//
// どのマイコンでも同じファイルのまま書き込める。中身はボードで自動的に切り替わる。
//   USB キーボードになる : ESP32-S3 / ESP32-S2 / Arduino Leonardo・Micro・Pro Micro（ATmega32U4）/ Raspberry Pi Pico
//   Bluetooth キーボード : 普通の ESP32（ESP32 DevKit / WROOM-32）・ESP32-C3・ESP32-C6
//                          → PC の Bluetooth 設定で「Ennichi Keys」とペアリングする。USB は給電だけ。
//   使えない             : Arduino Uno / Nano（USB キーボードになれない）
//
// ESP32-S3 / S2 の注意: ツール › USB Mode を「USB-OTG (TinyUSB)」にし、ボードの「USB」と書かれた口で PC につなぐ。

// ───── ピン（ボタンをつなぐ場所）。違うピンにつないだらここだけ変える ─────
#if defined(CONFIG_IDF_TARGET_ESP32S3) || defined(CONFIG_IDF_TARGET_ESP32S2)
const int ESCAPE_PIN = 4, F12_PIN = 5;
#elif defined(CONFIG_IDF_TARGET_ESP32C3) || defined(CONFIG_IDF_TARGET_ESP32C6)
const int ESCAPE_PIN = 4, F12_PIN = 5;
#elif defined(ARDUINO_ARCH_ESP32)
const int ESCAPE_PIN = 25, F12_PIN = 26;   // ESP32 DevKit: 基板に「D25」「D26」「25」「26」などと書いてある足
#elif defined(ARDUINO_ARCH_RP2040)
const int ESCAPE_PIN = 2, F12_PIN = 3;     // Pico: GP2 / GP3
#else
const int ESCAPE_PIN = 2, F12_PIN = 3;     // Leonardo / Pro Micro: 2 / 3
#endif

const unsigned long DEBOUNCE_MS = 30;      // チャタリング（押した瞬間のガタつき）を無視する時間
const unsigned long HOLD_MS = 40;          // キーを押している時間

// ───── キーボードの種類をボードで選ぶ ─────
#if defined(CONFIG_IDF_TARGET_ESP32S3) || defined(CONFIG_IDF_TARGET_ESP32S2)
  #define KEYS_USB_ESP32 1
#elif defined(ARDUINO_ARCH_ESP32)
  #define KEYS_BLE 1
#elif defined(USBCON) || defined(ARDUINO_ARCH_RP2040) || defined(ARDUINO_ARCH_SAMD)
  #define KEYS_USB_ARDUINO 1
#else
  #error "このボードは USB キーボードになれません（Uno / Nano など）。ESP32 / ESP32-S3 / Leonardo / Pro Micro / Pico を使ってください。"
#endif

// HID の使用コード（USB の規格で決まっている番号）
const uint8_t HID_ESCAPE = 0x29, HID_F12 = 0x45;

#if KEYS_USB_ESP32
#include "USB.h"
#include "USBHIDKeyboard.h"
USBHIDKeyboard keyboard;
void keysBegin() { keyboard.begin(); USB.begin(); }
bool keysReady() { return true; }
void keysTap(uint8_t hid) {
  KeyReport report = {0, 0, {hid, 0, 0, 0, 0, 0}};
  keyboard.sendReport(&report); delay(HOLD_MS);
  keyboard.releaseAll();
}

#elif KEYS_USB_ARDUINO
#include <Keyboard.h>
void keysBegin() { Keyboard.begin(); }
bool keysReady() { return true; }
void keysTap(uint8_t hid) {
  // Keyboard.h は「HID 番号 + 136」で生のキーを送る
  Keyboard.press(hid + 136); delay(HOLD_MS); Keyboard.releaseAll();
}

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
  security->setAuthenticationMode(ESP_LE_AUTH_BOND);    // 一度ペアリングすれば次から自動でつながる
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
  // Bluetooth 未接続の間は LED をゆっくり点滅させる
  if (LED >= 0 && !connected) digitalWrite(LED, (now / 500) % 2);
#endif
  delay(2);
}
