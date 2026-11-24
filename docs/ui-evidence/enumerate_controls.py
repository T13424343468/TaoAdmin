#!/usr/bin/env python3
"""控件树全量枚举：逐页枚举可见 button/input/select/switch/radio/checkbox 及 disabled 状态。
用法：python3 enumerate_controls.py page1 page2 ...（页面 key 见 PAGES 映射）
输出：docs/ui-evidence/controls/<key>.json
"""
import json
import subprocess
import sys
import time
import urllib.request

BASE = "http://127.0.0.1:10086/command"
SESSION = "taoadmin-ui2"
OUT_DIR = "/root/workspace/6ba46391-d241-4ac4-a098-8f7eaec431c5/TaoAdmin/docs/ui-evidence/controls"

PAGES = {
    "dashboard": "/dashboard",
    "codegen": "/system/codegen",
    "loginlog": "/system/loginlog",
    "file": "/file",
    "auditlog": "/system/auditlog",
    "notifychannel": "/system/notifychannel",
    "loginsecurity": "/system/loginsecurity",
    "region": "/system/region",
    "monitor-resource": "/monitor/resource",
    "monitor-online": "/monitor/online",
    "monitor-service": "/monitor/service",
    "monitor-log": "/monitor/log",
    "monitor-apidocs": "/monitor/apidocs",
    "job": "/task/job",
    "user": "/system/user",
    "role": "/system/role",
    "menu": "/system/menu",
    "api": "/system/api",
    "siteconfig": "/system/siteconfig",
    "announcement": "/system/announcement",
    "pwdpolicy": "/system/pwdpolicy",
}

ENUM_CODE = """
(() => {
  const vis = (el) => {
    const r = el.getBoundingClientRect();
    if (r.width <= 0 || r.height <= 0) return false;
    const s = getComputedStyle(el);
    return s.visibility !== 'hidden' && s.display !== 'none';
  };
  const info = (el, kind) => ({
    kind,
    text: (el.innerText || el.value || el.getAttribute('aria-label') || '').trim().slice(0, 40),
    placeholder: el.getAttribute('placeholder') || null,
    disabled: el.disabled === true || el.getAttribute('aria-disabled') === 'true' || el.classList.contains('ant-btn-disabled') || el.classList.contains('ant-switch-disabled'),
    checked: el.checked === true || el.getAttribute('aria-checked') === 'true',
  });
  const out = [];
  document.querySelectorAll('button, .ant-btn').forEach((el) => { if (vis(el)) out.push(info(el, 'button')); });
  document.querySelectorAll('input:not([type=hidden]), textarea').forEach((el) => { if (vis(el)) out.push(info(el, el.type === 'checkbox' || el.type === 'radio' ? el.type : 'input')); });
  document.querySelectorAll('.ant-select').forEach((el) => { if (vis(el)) out.push({ kind: 'select', text: (el.innerText || '').trim().slice(0, 40), disabled: el.classList.contains('ant-select-disabled'), checked: false }); });
  document.querySelectorAll('.ant-switch').forEach((el) => { if (vis(el)) out.push({ kind: 'switch', text: el.getAttribute('aria-label') || (el.innerText || '').trim().slice(0, 40), disabled: el.classList.contains('ant-switch-disabled'), checked: el.classList.contains('ant-switch-checked') }); });
  document.querySelectorAll('.ant-radio-wrapper, .ant-checkbox-wrapper').forEach((el) => { if (vis(el)) out.push({ kind: el.classList.contains('ant-radio-wrapper') ? 'radio' : 'checkbox', text: (el.innerText || '').trim().slice(0, 40), disabled: el.classList.contains('ant-radio-wrapper-disabled') || el.classList.contains('ant-checkbox-wrapper-disabled'), checked: el.classList.contains('ant-radio-wrapper-checked') || el.classList.contains('ant-checkbox-wrapper-checked') }); });
  return JSON.stringify(out);
})()
"""


def call(action, args):
    body = json.dumps({"action": action, "args": args, "session": SESSION}).encode()
    req = urllib.request.Request(BASE, data=body, headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode())


def main(keys):
    import os

    os.makedirs(OUT_DIR, exist_ok=True)
    results = {}
    for key in keys:
        url_path = PAGES[key]
        try:
            call("navigate", {"url": f"http://127.0.0.1:5173{url_path}"})
            time.sleep(3.5)
            r = call("evaluate", {"code": ENUM_CODE})
            controls = json.loads(r["data"]["value"])
            path = f"{OUT_DIR}/{key}.json"
            with open(path, "w", encoding="utf-8") as f:
                json.dump({"page": key, "route": url_path, "controlCount": len(controls), "controls": controls}, f, ensure_ascii=False, indent=2)
            results[key] = len(controls)
            print(f"{key}: {len(controls)} controls")
        except Exception as exc:  # noqa: BLE001
            results[key] = f"ERROR: {exc}"
            print(f"{key}: ERROR {exc}")
    print(json.dumps(results, ensure_ascii=False))


if __name__ == "__main__":
    main(sys.argv[1:])
