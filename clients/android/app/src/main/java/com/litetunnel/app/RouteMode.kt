package com.litetunnel.app

enum class RouteMode(val key: String, val label: String) {
    GLOBAL("global", "全局代理"),
    SMART("smart", "智能分流");

    companion object {
        fun from(value: String?): RouteMode =
            values().firstOrNull { it.key == value } ?: GLOBAL
    }
}
