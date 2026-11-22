package com.litetunnel.app

import android.content.res.ColorStateList
import android.view.LayoutInflater
import android.view.ViewGroup
import androidx.recyclerview.widget.RecyclerView
import com.litetunnel.app.databinding.ItemProfileBinding

class ProfileAdapter(
    private val onClick: (Profile) -> Unit,
    private val onLongClick: (Profile) -> Unit
) : RecyclerView.Adapter<ProfileAdapter.ViewHolder>() {

    private var items: List<Profile> = emptyList()
    private var activeId: String? = null
    private val latencies = mutableMapOf<String, Long?>()

    fun submit(profiles: List<Profile>, activeProfileId: String?) {
        items = profiles
        activeId = activeProfileId
        notifyDataSetChanged()
    }

    fun updateLatency(profileId: String, latencyMs: Long?) {
        latencies[profileId] = latencyMs
        val index = items.indexOfFirst { it.id == profileId }
        if (index >= 0) notifyItemChanged(index)
    }

    override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
        val binding = ItemProfileBinding.inflate(
            LayoutInflater.from(parent.context), parent, false
        )
        return ViewHolder(binding)
    }

    override fun getItemCount(): Int = items.size

    override fun onBindViewHolder(holder: ViewHolder, position: Int) {
        val profile = items[position]
        val context = holder.itemView.context
        val connected = profile.id == activeId

        holder.binding.nameText.text = profile.name
        holder.binding.endpointText.text = serverSummaryOf(profile.config)
        holder.binding.stateText.text = if (connected) "已连接" else "点击连接"
        holder.binding.stateText.setTextColor(
            context.getColor(if (connected) R.color.success else R.color.muted)
        )
        holder.binding.latencyText.text = formatLatency(latencies[profile.id])
        holder.binding.latencyText.setTextColor(context.getColor(R.color.muted))
        holder.binding.dot.backgroundTintList = ColorStateList.valueOf(
            context.getColor(if (connected) R.color.success else R.color.muted)
        )
        holder.binding.card.strokeColor =
            context.getColor(if (connected) R.color.brand else R.color.outline)
        val density = context.resources.displayMetrics.density
        holder.binding.card.strokeWidth = ((if (connected) 2 else 1) * density).toInt()

        holder.itemView.setOnClickListener { onClick(profile) }
        holder.itemView.setOnLongClickListener {
            onLongClick(profile)
            true
        }
    }

    class ViewHolder(val binding: ItemProfileBinding) : RecyclerView.ViewHolder(binding.root)
}
