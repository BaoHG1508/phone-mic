package com.phonemic.app.service

import kotlinx.coroutines.flow.MutableStateFlow

enum class Status { IDLE, STARTING, STREAMING, RECONNECTING, ERROR }

val Status.isActive: Boolean
    get() = this == Status.STARTING || this == Status.STREAMING || this == Status.RECONNECTING

data class StreamState(
    val status: Status = Status.IDLE,
    val config: StreamConfig? = null,
    val muted: Boolean = false,
    /** For UDP there is no connection; this is "the PC answered a PING within the last few seconds". */
    val pcReachable: Boolean = true,
    val levelDb: Float = -96f,
    val packetsSent: Long = 0,
    val kbps: Int = 0,
    val error: String? = null,
    val startedAtMs: Long = 0,
)

/** Process-wide state shared between the service and the UI (both live in the same process). */
object StreamStateHolder {
    val state = MutableStateFlow(StreamState())
}
